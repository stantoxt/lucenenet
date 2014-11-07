﻿using System;

namespace org.apache.lucene.analysis.position
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

	using PositionIncrementAttribute = org.apache.lucene.analysis.tokenattributes.PositionIncrementAttribute;

	/// <summary>
	/// Set the positionIncrement of all tokens to the "positionIncrement",
	/// except the first return token which retains its original positionIncrement value.
	/// The default positionIncrement value is zero. </summary>
	/// @deprecated (4.4) PositionFilter makes <seealso cref="TokenStream"/> graphs inconsistent
	///             which can cause highlighting bugs. Its main use-case being to make
	///             <a href="{@docRoot}/../queryparser/overview-summary.html">QueryParser</a>
	///             generate boolean queries instead of phrase queries, it is now advised to use
	///             {@code QueryParser.setAutoGeneratePhraseQueries(boolean)}
	///             (for simple cases) or to override {@code QueryParser.newFieldQuery}. 
	[Obsolete("(4.4) PositionFilter makes <seealso cref="TokenStream"/> graphs inconsistent")]
	public sealed class PositionFilter : TokenFilter
	{

	  /// <summary>
	  /// Position increment to assign to all but the first token - default = 0 </summary>
	  private readonly int positionIncrement;

	  /// <summary>
	  /// The first token must have non-zero positionIncrement * </summary>
	  private bool firstTokenPositioned = false;

	  private PositionIncrementAttribute posIncrAtt = addAttribute(typeof(PositionIncrementAttribute));

	  /// <summary>
	  /// Constructs a PositionFilter that assigns a position increment of zero to
	  /// all but the first token from the given input stream.
	  /// </summary>
	  /// <param name="input"> the input stream </param>
//JAVA TO C# CONVERTER WARNING: 'final' parameters are not available in .NET:
//ORIGINAL LINE: public PositionFilter(final org.apache.lucene.analysis.TokenStream input)
	  public PositionFilter(TokenStream input) : this(input, 0)
	  {
	  }

	  /// <summary>
	  /// Constructs a PositionFilter that assigns the given position increment to
	  /// all but the first token from the given input stream.
	  /// </summary>
	  /// <param name="input"> the input stream </param>
	  /// <param name="positionIncrement"> position increment to assign to all but the first
	  ///  token from the input stream </param>
//JAVA TO C# CONVERTER WARNING: 'final' parameters are not available in .NET:
//ORIGINAL LINE: public PositionFilter(final org.apache.lucene.analysis.TokenStream input, final int positionIncrement)
	  public PositionFilter(TokenStream input, int positionIncrement) : base(input)
	  {
		if (positionIncrement < 0)
		{
		  throw new System.ArgumentException("positionIncrement may not be negative");
		}
		this.positionIncrement = positionIncrement;
	  }

//JAVA TO C# CONVERTER WARNING: Method 'throws' clauses are not available in .NET:
//ORIGINAL LINE: @Override public final boolean incrementToken() throws java.io.IOException
	  public override bool incrementToken()
	  {
		if (input.incrementToken())
		{
		  if (firstTokenPositioned)
		  {
			posIncrAtt.PositionIncrement = positionIncrement;
		  }
		  else
		  {
			firstTokenPositioned = true;
		  }
		  return true;
		}
		else
		{
		  return false;
		}
	  }

//JAVA TO C# CONVERTER WARNING: Method 'throws' clauses are not available in .NET:
//ORIGINAL LINE: @Override public void reset() throws java.io.IOException
	  public override void reset()
	  {
		base.reset();
		firstTokenPositioned = false;
	  }
	}

}