﻿using System;
using System.Diagnostics;

namespace org.apache.lucene.analysis.synonym
{

	/*
	 * Licensed to the Apache Software Foundation (ASF) under one or more
	 * contributor license agreements.  See the NOTICE file distributed with
	 * this work for additional information regarding copyright ownership.
	 * The ASF licenses this file to You under the Apache License, Version 2.0
	 * (the "License"); you may not use this file except in compliance with
	 * the License.  You may obtain a copy of the License at
	 *
	 *     http://www.apache.org/licenses/LICENSE-2.0
	 *
	 * Unless required by applicable law or agreed to in writing, software
	 * distributed under the License is distributed on an "AS IS" BASIS,
	 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
	 * See the License for the specific language governing permissions and
	 * limitations under the License.
	 */

	using CharTermAttribute = org.apache.lucene.analysis.tokenattributes.CharTermAttribute;
	using OffsetAttribute = org.apache.lucene.analysis.tokenattributes.OffsetAttribute;
	using PositionIncrementAttribute = org.apache.lucene.analysis.tokenattributes.PositionIncrementAttribute;
	using PositionLengthAttribute = org.apache.lucene.analysis.tokenattributes.PositionLengthAttribute;
	using TypeAttribute = org.apache.lucene.analysis.tokenattributes.TypeAttribute;
	using ByteArrayDataInput = org.apache.lucene.store.ByteArrayDataInput;
	using ArrayUtil = org.apache.lucene.util.ArrayUtil;
	using AttributeSource = org.apache.lucene.util.AttributeSource;
	using BytesRef = org.apache.lucene.util.BytesRef;
	using CharsRef = org.apache.lucene.util.CharsRef;
	using RamUsageEstimator = org.apache.lucene.util.RamUsageEstimator;
	using UnicodeUtil = org.apache.lucene.util.UnicodeUtil;
	using FST = org.apache.lucene.util.fst.FST;

	/// <summary>
	/// Matches single or multi word synonyms in a token stream.
	/// This token stream cannot properly handle position
	/// increments != 1, ie, you should place this filter before
	/// filtering out stop words.
	/// 
	/// <para>Note that with the current implementation, parsing is
	/// greedy, so whenever multiple parses would apply, the rule
	/// starting the earliest and parsing the most tokens wins.
	/// For example if you have these rules:
	///      
	/// <pre>
	///   a -> x
	///   a b -> y
	///   b c d -> z
	/// </pre>
	/// 
	/// Then input <code>a b c d e</code> parses to <code>y b c
	/// d</code>, ie the 2nd rule "wins" because it started
	/// earliest and matched the most input tokens of other rules
	/// starting at that point.</para>
	/// 
	/// <para>A future improvement to this filter could allow
	/// non-greedy parsing, such that the 3rd rule would win, and
	/// also separately allow multiple parses, such that all 3
	/// rules would match, perhaps even on a rule by rule
	/// basis.</para>
	/// 
	/// <para><b>NOTE</b>: when a match occurs, the output tokens
	/// associated with the matching rule are "stacked" on top of
	/// the input stream (if the rule had
	/// <code>keepOrig=true</code>) and also on top of another
	/// matched rule's output tokens.  This is not a correct
	/// solution, as really the output should be an arbitrary
	/// graph/lattice.  For example, with the above match, you
	/// would expect an exact <code>PhraseQuery</code> <code>"y b
	/// c"</code> to match the parsed tokens, but it will fail to
	/// do so.  This limitation is necessary because Lucene's
	/// TokenStream (and index) cannot yet represent an arbitrary
	/// graph.</para>
	/// 
	/// <para><b>NOTE</b>: If multiple incoming tokens arrive on the
	/// same position, only the first token at that position is
	/// used for parsing.  Subsequent tokens simply pass through
	/// and are not parsed.  A future improvement would be to
	/// allow these tokens to also be matched.</para>
	/// </summary>

	// TODO: maybe we should resolve token -> wordID then run
	// FST on wordIDs, for better perf?

	// TODO: a more efficient approach would be Aho/Corasick's
	// algorithm
	// http://en.wikipedia.org/wiki/Aho%E2%80%93Corasick_string_matching_algorithm
	// It improves over the current approach here
	// because it does not fully re-start matching at every
	// token.  For example if one pattern is "a b c x"
	// and another is "b c d" and the input is "a b c d", on
	// trying to parse "a b c x" but failing when you got to x,
	// rather than starting over again your really should
	// immediately recognize that "b c d" matches at the next
	// input.  I suspect this won't matter that much in
	// practice, but it's possible on some set of synonyms it
	// will.  We'd have to modify Aho/Corasick to enforce our
	// conflict resolving (eg greedy matching) because that algo
	// finds all matches.  This really amounts to adding a .*
	// closure to the FST and then determinizing it.

	public sealed class SynonymFilter : TokenFilter
	{

	  public const string TYPE_SYNONYM = "SYNONYM";

	  private readonly SynonymMap synonyms;

	  private readonly bool ignoreCase;
	  private readonly int rollBufferSize;

	  private int captureCount;

	  // TODO: we should set PositionLengthAttr too...

	  private readonly CharTermAttribute termAtt = addAttribute(typeof(CharTermAttribute));
	  private readonly PositionIncrementAttribute posIncrAtt = addAttribute(typeof(PositionIncrementAttribute));
	  private readonly PositionLengthAttribute posLenAtt = addAttribute(typeof(PositionLengthAttribute));
	  private readonly TypeAttribute typeAtt = addAttribute(typeof(TypeAttribute));
	  private readonly OffsetAttribute offsetAtt = addAttribute(typeof(OffsetAttribute));

	  // How many future input tokens have already been matched
	  // to a synonym; because the matching is "greedy" we don't
	  // try to do any more matching for such tokens:
	  private int inputSkipCount;

	  // Hold all buffered (read ahead) stacked input tokens for
	  // a future position.  When multiple tokens are at the
	  // same position, we only store (and match against) the
	  // term for the first token at the position, but capture
	  // state for (and enumerate) all other tokens at this
	  // position:
	  private class PendingInput
	  {
		internal readonly CharsRef term = new CharsRef();
		internal AttributeSource.State state;
		internal bool keepOrig;
		internal bool matched;
		internal bool consumed = true;
		internal int startOffset;
		internal int endOffset;

		public virtual void reset()
		{
		  state = null;
		  consumed = true;
		  keepOrig = false;
		  matched = false;
		}
	  }

	  // Rolling buffer, holding pending input tokens we had to
	  // clone because we needed to look ahead, indexed by
	  // position:
	  private readonly PendingInput[] futureInputs;

	  // Holds pending output synonyms for one future position:
	  private class PendingOutputs
	  {
		internal CharsRef[] outputs;
		internal int[] endOffsets;
		internal int[] posLengths;
		internal int upto;
		internal int count;
		internal int posIncr = 1;
		internal int lastEndOffset;
		internal int lastPosLength;

		public PendingOutputs()
		{
		  outputs = new CharsRef[1];
		  endOffsets = new int[1];
		  posLengths = new int[1];
		}

		public virtual void reset()
		{
		  upto = count = 0;
		  posIncr = 1;
		}

		public virtual CharsRef pullNext()
		{
		  Debug.Assert(upto < count);
		  lastEndOffset = endOffsets[upto];
		  lastPosLength = posLengths[upto];
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final org.apache.lucene.util.CharsRef result = outputs[upto++];
		  CharsRef result = outputs[upto++];
		  posIncr = 0;
		  if (upto == count)
		  {
			reset();
		  }
		  return result;
		}

		public virtual int LastEndOffset
		{
			get
			{
			  return lastEndOffset;
			}
		}

		public virtual int LastPosLength
		{
			get
			{
			  return lastPosLength;
			}
		}

		public virtual void add(char[] output, int offset, int len, int endOffset, int posLength)
		{
		  if (count == outputs.Length)
		  {
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final org.apache.lucene.util.CharsRef[] next = new org.apache.lucene.util.CharsRef[org.apache.lucene.util.ArrayUtil.oversize(1+count, org.apache.lucene.util.RamUsageEstimator.NUM_BYTES_OBJECT_REF)];
			CharsRef[] next = new CharsRef[ArrayUtil.oversize(1 + count, RamUsageEstimator.NUM_BYTES_OBJECT_REF)];
			Array.Copy(outputs, 0, next, 0, count);
			outputs = next;
		  }
		  if (count == endOffsets.Length)
		  {
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final int[] next = new int[org.apache.lucene.util.ArrayUtil.oversize(1+count, org.apache.lucene.util.RamUsageEstimator.NUM_BYTES_INT)];
			int[] next = new int[ArrayUtil.oversize(1 + count, RamUsageEstimator.NUM_BYTES_INT)];
			Array.Copy(endOffsets, 0, next, 0, count);
			endOffsets = next;
		  }
		  if (count == posLengths.Length)
		  {
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final int[] next = new int[org.apache.lucene.util.ArrayUtil.oversize(1+count, org.apache.lucene.util.RamUsageEstimator.NUM_BYTES_INT)];
			int[] next = new int[ArrayUtil.oversize(1 + count, RamUsageEstimator.NUM_BYTES_INT)];
			Array.Copy(posLengths, 0, next, 0, count);
			posLengths = next;
		  }
		  if (outputs[count] == null)
		  {
			outputs[count] = new CharsRef();
		  }
		  outputs[count].copyChars(output, offset, len);
		  // endOffset can be -1, in which case we should simply
		  // use the endOffset of the input token, or X >= 0, in
		  // which case we use X as the endOffset for this output
		  endOffsets[count] = endOffset;
		  posLengths[count] = posLength;
		  count++;
		}
	  }

	  private readonly ByteArrayDataInput bytesReader = new ByteArrayDataInput();

	  // Rolling buffer, holding stack of pending synonym
	  // outputs, indexed by position:
	  private readonly PendingOutputs[] futureOutputs;

	  // Where (in rolling buffers) to write next input saved state:
	  private int nextWrite;

	  // Where (in rolling buffers) to read next input saved state:
	  private int nextRead;

	  // True once we've read last token
	  private bool finished;

	  private readonly FST.Arc<BytesRef> scratchArc;

	  private readonly FST<BytesRef> fst;

	  private readonly FST.BytesReader fstReader;


	  private readonly BytesRef scratchBytes = new BytesRef();
	  private readonly CharsRef scratchChars = new CharsRef();

	  /// <param name="input"> input tokenstream </param>
	  /// <param name="synonyms"> synonym map </param>
	  /// <param name="ignoreCase"> case-folds input for matching with <seealso cref="Character#toLowerCase(int)"/>.
	  ///                   Note, if you set this to true, its your responsibility to lowercase
	  ///                   the input entries when you create the <seealso cref="SynonymMap"/> </param>
	  public SynonymFilter(TokenStream input, SynonymMap synonyms, bool ignoreCase) : base(input)
	  {
		this.synonyms = synonyms;
		this.ignoreCase = ignoreCase;
		this.fst = synonyms.fst;
		if (fst == null)
		{
		  throw new System.ArgumentException("fst must be non-null");
		}
		this.fstReader = fst.BytesReader;

		// Must be 1+ so that when roll buffer is at full
		// lookahead we can distinguish this full buffer from
		// the empty buffer:
		rollBufferSize = 1 + synonyms.maxHorizontalContext;

		futureInputs = new PendingInput[rollBufferSize];
		futureOutputs = new PendingOutputs[rollBufferSize];
		for (int pos = 0;pos < rollBufferSize;pos++)
		{
		  futureInputs[pos] = new PendingInput();
		  futureOutputs[pos] = new PendingOutputs();
		}

		//System.out.println("FSTFilt maxH=" + synonyms.maxHorizontalContext);

		scratchArc = new FST.Arc<>();
	  }

	  private void capture()
	  {
		captureCount++;
		//System.out.println("  capture slot=" + nextWrite);
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final PendingInput input = futureInputs[nextWrite];
		PendingInput input = futureInputs[nextWrite];

		input.state = captureState();
		input.consumed = false;
		input.term.copyChars(termAtt.buffer(), 0, termAtt.length());

		nextWrite = rollIncr(nextWrite);

		// Buffer head should never catch up to tail:
		Debug.Assert(nextWrite != nextRead);
	  }

	  /*
	   This is the core of this TokenFilter: it locates the
	   synonym matches and buffers up the results into
	   futureInputs/Outputs.
	
	   NOTE: this calls input.incrementToken and does not
	   capture the state if no further tokens were checked.  So
	   caller must then forward state to our caller, or capture:
	  */
	  private int lastStartOffset;
	  private int lastEndOffset;

//JAVA TO C# CONVERTER WARNING: Method 'throws' clauses are not available in .NET:
//ORIGINAL LINE: private void parse() throws java.io.IOException
	  private void parse()
	  {
		//System.out.println("\nS: parse");

		Debug.Assert(inputSkipCount == 0);

		int curNextRead = nextRead;

		// Holds the longest match we've seen so far:
		BytesRef matchOutput = null;
		int matchInputLength = 0;
		int matchEndOffset = -1;

		BytesRef pendingOutput = fst.outputs.NoOutput;
		fst.getFirstArc(scratchArc);

		Debug.Assert(scratchArc.output == fst.outputs.NoOutput);

		int tokenCount = 0;

		while (true)
		{

		  // Pull next token's chars:
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final char[] buffer;
		  char[] buffer;
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final int bufferLen;
		  int bufferLen;
		  //System.out.println("  cycle nextRead=" + curNextRead + " nextWrite=" + nextWrite);

		  int inputEndOffset = 0;

		  if (curNextRead == nextWrite)
		  {

			// We used up our lookahead buffer of input tokens
			// -- pull next real input token:

			if (finished)
			{
			  break;
			}
			else
			{
			  //System.out.println("  input.incrToken");
			  Debug.Assert(futureInputs[nextWrite].consumed);
			  // Not correct: a syn match whose output is longer
			  // than its input can set future inputs keepOrig
			  // to true:
			  //assert !futureInputs[nextWrite].keepOrig;
			  if (input.incrementToken())
			  {
				buffer = termAtt.buffer();
				bufferLen = termAtt.length();
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final PendingInput input = futureInputs[nextWrite];
				PendingInput input = futureInputs[nextWrite];
				lastStartOffset = input.startOffset = offsetAtt.startOffset();
				lastEndOffset = input.endOffset = offsetAtt.endOffset();
				inputEndOffset = input.endOffset;
				//System.out.println("  new token=" + new String(buffer, 0, bufferLen));
				if (nextRead != nextWrite)
				{
				  capture();
				}
				else
				{
				  input.consumed = false;
				}

			  }
			  else
			  {
				// No more input tokens
				//System.out.println("      set end");
				finished = true;
				break;
			  }
			}
		  }
		  else
		  {
			// Still in our lookahead
			buffer = futureInputs[curNextRead].term.chars;
			bufferLen = futureInputs[curNextRead].term.length;
			inputEndOffset = futureInputs[curNextRead].endOffset;
			//System.out.println("  old token=" + new String(buffer, 0, bufferLen));
		  }

		  tokenCount++;

		  // Run each char in this token through the FST:
		  int bufUpto = 0;
		  while (bufUpto < bufferLen)
		  {
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final int codePoint = Character.codePointAt(buffer, bufUpto, bufferLen);
			int codePoint = char.codePointAt(buffer, bufUpto, bufferLen);
			if (fst.findTargetArc(ignoreCase ? char.ToLower(codePoint) : codePoint, scratchArc, scratchArc, fstReader) == null)
			{
			  //System.out.println("    stop");
			  goto byTokenBreak;
			}

			// Accum the output
			pendingOutput = fst.outputs.add(pendingOutput, scratchArc.output);
			//System.out.println("    char=" + buffer[bufUpto] + " output=" + pendingOutput + " arc.output=" + scratchArc.output);
			bufUpto += char.charCount(codePoint);
		  }

		  // OK, entire token matched; now see if this is a final
		  // state:
		  if (scratchArc.Final)
		  {
			matchOutput = fst.outputs.add(pendingOutput, scratchArc.nextFinalOutput);
			matchInputLength = tokenCount;
			matchEndOffset = inputEndOffset;
			//System.out.println("  found matchLength=" + matchInputLength + " output=" + matchOutput);
		  }

		  // See if the FST wants to continue matching (ie, needs to
		  // see the next input token):
		  if (fst.findTargetArc(SynonymMap.WORD_SEPARATOR, scratchArc, scratchArc, fstReader) == null)
		  {
			// No further rules can match here; we're done
			// searching for matching rules starting at the
			// current input position.
			break;
		  }
		  else
		  {
			// More matching is possible -- accum the output (if
			// any) of the WORD_SEP arc:
			pendingOutput = fst.outputs.add(pendingOutput, scratchArc.output);
			if (nextRead == nextWrite)
			{
			  capture();
			}
		  }

		  curNextRead = rollIncr(curNextRead);
			byTokenContinue:;
		}
		byTokenBreak:

		if (nextRead == nextWrite && !finished)
		{
		  //System.out.println("  skip write slot=" + nextWrite);
		  nextWrite = rollIncr(nextWrite);
		}

		if (matchOutput != null)
		{
		  //System.out.println("  add matchLength=" + matchInputLength + " output=" + matchOutput);
		  inputSkipCount = matchInputLength;
		  addOutput(matchOutput, matchInputLength, matchEndOffset);
		}
		else if (nextRead != nextWrite)
		{
		  // Even though we had no match here, we set to 1
		  // because we need to skip current input token before
		  // trying to match again:
		  inputSkipCount = 1;
		}
		else
		{
		  Debug.Assert(finished);
		}

		//System.out.println("  parse done inputSkipCount=" + inputSkipCount + " nextRead=" + nextRead + " nextWrite=" + nextWrite);
	  }

	  // Interleaves all output tokens onto the futureOutputs:
	  private void addOutput(BytesRef bytes, int matchInputLength, int matchEndOffset)
	  {
		bytesReader.reset(bytes.bytes, bytes.offset, bytes.length);

//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final int code = bytesReader.readVInt();
		int code = bytesReader.readVInt();
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final boolean keepOrig = (code & 0x1) == 0;
		bool keepOrig = (code & 0x1) == 0;
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final int count = code >>> 1;
		int count = (int)((uint)code >> 1);
		//System.out.println("  addOutput count=" + count + " keepOrig=" + keepOrig);
		for (int outputIDX = 0;outputIDX < count;outputIDX++)
		{
		  synonyms.words.get(bytesReader.readVInt(), scratchBytes);
		  //System.out.println("    outIDX=" + outputIDX + " bytes=" + scratchBytes.length);
		  UnicodeUtil.UTF8toUTF16(scratchBytes, scratchChars);
		  int lastStart = scratchChars.offset;
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final int chEnd = lastStart + scratchChars.length;
		  int chEnd = lastStart + scratchChars.length;
		  int outputUpto = nextRead;
		  for (int chIDX = lastStart;chIDX <= chEnd;chIDX++)
		  {
			if (chIDX == chEnd || scratchChars.chars[chIDX] == SynonymMap.WORD_SEPARATOR)
			{
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final int outputLen = chIDX - lastStart;
			  int outputLen = chIDX - lastStart;
			  // Caller is not allowed to have empty string in
			  // the output:
			  Debug.Assert(outputLen > 0, "output contains empty string: " + scratchChars);
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final int endOffset;
			  int endOffset;
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final int posLen;
			  int posLen;
			  if (chIDX == chEnd && lastStart == scratchChars.offset)
			  {
				// This rule had a single output token, so, we set
				// this output's endOffset to the current
				// endOffset (ie, endOffset of the last input
				// token it matched):
				endOffset = matchEndOffset;
				posLen = keepOrig ? matchInputLength : 1;
			  }
			  else
			  {
				// This rule has more than one output token; we
				// can't pick any particular endOffset for this
				// case, so, we inherit the endOffset for the
				// input token which this output overlaps:
				endOffset = -1;
				posLen = 1;
			  }
			  futureOutputs[outputUpto].add(scratchChars.chars, lastStart, outputLen, endOffset, posLen);
			  //System.out.println("      " + new String(scratchChars.chars, lastStart, outputLen) + " outputUpto=" + outputUpto);
			  lastStart = 1 + chIDX;
			  //System.out.println("  slot=" + outputUpto + " keepOrig=" + keepOrig);
			  outputUpto = rollIncr(outputUpto);
			  Debug.Assert(futureOutputs[outputUpto].posIncr == 1, "outputUpto=" + outputUpto + " vs nextWrite=" + nextWrite);
			}
		  }
		}

		int upto = nextRead;
		for (int idx = 0;idx < matchInputLength;idx++)
		{
		  futureInputs[upto].keepOrig |= keepOrig;
		  futureInputs[upto].matched = true;
		  upto = rollIncr(upto);
		}
	  }

	  // ++ mod rollBufferSize
	  private int rollIncr(int count)
	  {
		count++;
		if (count == rollBufferSize)
		{
		  return 0;
		}
		else
		{
		  return count;
		}
	  }

	  // for testing
	  internal int CaptureCount
	  {
		  get
		  {
			return captureCount;
		  }
	  }

//JAVA TO C# CONVERTER WARNING: Method 'throws' clauses are not available in .NET:
//ORIGINAL LINE: @Override public boolean incrementToken() throws java.io.IOException
	  public override bool incrementToken()
	  {

		//System.out.println("\nS: incrToken inputSkipCount=" + inputSkipCount + " nextRead=" + nextRead + " nextWrite=" + nextWrite);

		while (true)
		{

		  // First play back any buffered future inputs/outputs
		  // w/o running parsing again:
		  while (inputSkipCount != 0)
		  {

			// At each position, we first output the original
			// token

			// TODO: maybe just a PendingState class, holding
			// both input & outputs?
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final PendingInput input = futureInputs[nextRead];
			PendingInput input = futureInputs[nextRead];
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final PendingOutputs outputs = futureOutputs[nextRead];
			PendingOutputs outputs = futureOutputs[nextRead];

			//System.out.println("  cycle nextRead=" + nextRead + " nextWrite=" + nextWrite + " inputSkipCount="+ inputSkipCount + " input.keepOrig=" + input.keepOrig + " input.consumed=" + input.consumed + " input.state=" + input.state);

			if (!input.consumed && (input.keepOrig || !input.matched))
			{
			  if (input.state != null)
			  {
				// Return a previously saved token (because we
				// had to lookahead):
				restoreState(input.state);
			  }
			  else
			  {
				// Pass-through case: return token we just pulled
				// but didn't capture:
				Debug.Assert(inputSkipCount == 1, "inputSkipCount=" + inputSkipCount + " nextRead=" + nextRead);
			  }
			  input.reset();
			  if (outputs.count > 0)
			  {
				outputs.posIncr = 0;
			  }
			  else
			  {
				nextRead = rollIncr(nextRead);
				inputSkipCount--;
			  }
			  //System.out.println("  return token=" + termAtt.toString());
			  return true;
			}
			else if (outputs.upto < outputs.count)
			{
			  // Still have pending outputs to replay at this
			  // position
			  input.reset();
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final int posIncr = outputs.posIncr;
			  int posIncr = outputs.posIncr;
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final org.apache.lucene.util.CharsRef output = outputs.pullNext();
			  CharsRef output = outputs.pullNext();
			  clearAttributes();
			  termAtt.copyBuffer(output.chars, output.offset, output.length);
			  typeAtt.Type = TYPE_SYNONYM;
			  int endOffset = outputs.LastEndOffset;
			  if (endOffset == -1)
			  {
				endOffset = input.endOffset;
			  }
			  offsetAtt.setOffset(input.startOffset, endOffset);
			  posIncrAtt.PositionIncrement = posIncr;
			  posLenAtt.PositionLength = outputs.LastPosLength;
			  if (outputs.count == 0)
			  {
				// Done with the buffered input and all outputs at
				// this position
				nextRead = rollIncr(nextRead);
				inputSkipCount--;
			  }
			  //System.out.println("  return token=" + termAtt.toString());
			  return true;
			}
			else
			{
			  // Done with the buffered input and all outputs at
			  // this position
			  input.reset();
			  nextRead = rollIncr(nextRead);
			  inputSkipCount--;
			}
		  }

		  if (finished && nextRead == nextWrite)
		  {
			// End case: if any output syns went beyond end of
			// input stream, enumerate them now:
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final PendingOutputs outputs = futureOutputs[nextRead];
			PendingOutputs outputs = futureOutputs[nextRead];
			if (outputs.upto < outputs.count)
			{
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final int posIncr = outputs.posIncr;
			  int posIncr = outputs.posIncr;
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final org.apache.lucene.util.CharsRef output = outputs.pullNext();
			  CharsRef output = outputs.pullNext();
			  futureInputs[nextRead].reset();
			  if (outputs.count == 0)
			  {
				nextWrite = nextRead = rollIncr(nextRead);
			  }
			  clearAttributes();
			  // Keep offset from last input token:
			  offsetAtt.setOffset(lastStartOffset, lastEndOffset);
			  termAtt.copyBuffer(output.chars, output.offset, output.length);
			  typeAtt.Type = TYPE_SYNONYM;
			  //System.out.println("  set posIncr=" + outputs.posIncr + " outputs=" + outputs);
			  posIncrAtt.PositionIncrement = posIncr;
			  //System.out.println("  return token=" + termAtt.toString());
			  return true;
			}
			else
			{
			  return false;
			}
		  }

		  // Find new synonym matches:
		  parse();
		}
	  }

//JAVA TO C# CONVERTER WARNING: Method 'throws' clauses are not available in .NET:
//ORIGINAL LINE: @Override public void reset() throws java.io.IOException
	  public override void reset()
	  {
		base.reset();
		captureCount = 0;
		finished = false;
		inputSkipCount = 0;
		nextRead = nextWrite = 0;

		// In normal usage these resets would not be needed,
		// since they reset-as-they-are-consumed, but the app
		// may not consume all input tokens (or we might hit an
		// exception), in which case we have leftover state
		// here:
		foreach (PendingInput input in futureInputs)
		{
		  input.reset();
		}
		foreach (PendingOutputs output in futureOutputs)
		{
		  output.reset();
		}
	  }
	}

}