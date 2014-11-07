﻿/*
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
namespace org.apache.lucene.analysis.commongrams
{

	using PositionIncrementAttribute = org.apache.lucene.analysis.tokenattributes.PositionIncrementAttribute;
	using TypeAttribute = org.apache.lucene.analysis.tokenattributes.TypeAttribute;

//JAVA TO C# CONVERTER TODO TASK: This Java 'import static' statement cannot be converted to C#:
//	import static org.apache.lucene.analysis.commongrams.CommonGramsFilter.GRAM_TYPE;

	/// <summary>
	/// Wrap a CommonGramsFilter optimizing phrase queries by only returning single
	/// words when they are not a member of a bigram.
	/// 
	/// Example:
	/// <ul>
	/// <li>query input to CommonGramsFilter: "the rain in spain falls mainly"
	/// <li>output of CommomGramsFilter/input to CommonGramsQueryFilter:
	/// |"the, "the-rain"|"rain" "rain-in"|"in, "in-spain"|"spain"|"falls"|"mainly"
	/// <li>output of CommonGramsQueryFilter:"the-rain", "rain-in" ,"in-spain",
	/// "falls", "mainly"
	/// </ul>
	/// </summary>

	/*
	 * See:http://hudson.zones.apache.org/hudson/job/Lucene-trunk/javadoc//all/org/apache/lucene/analysis/TokenStream.html and
	 * http://svn.apache.org/viewvc/lucene/dev/trunk/lucene/src/java/org/apache/lucene/analysis/package.html?revision=718798
	 */
	public sealed class CommonGramsQueryFilter : TokenFilter
	{

	  private readonly TypeAttribute typeAttribute = addAttribute(typeof(TypeAttribute));
	  private readonly PositionIncrementAttribute posIncAttribute = addAttribute(typeof(PositionIncrementAttribute));

	  private State previous;
	  private string previousType;
	  private bool exhausted;

	  /// <summary>
	  /// Constructs a new CommonGramsQueryFilter based on the provided CommomGramsFilter 
	  /// </summary>
	  /// <param name="input"> CommonGramsFilter the QueryFilter will use </param>
	  public CommonGramsQueryFilter(CommonGramsFilter input) : base(input)
	  {
	  }

	  /// <summary>
	  /// {@inheritDoc}
	  /// </summary>
//JAVA TO C# CONVERTER WARNING: Method 'throws' clauses are not available in .NET:
//ORIGINAL LINE: @Override public void reset() throws java.io.IOException
	  public override void reset()
	  {
		base.reset();
		previous = null;
		previousType = null;
		exhausted = false;
	  }

	  /// <summary>
	  /// Output bigrams whenever possible to optimize queries. Only output unigrams
	  /// when they are not a member of a bigram. Example:
	  /// <ul>
	  /// <li>input: "the rain in spain falls mainly"
	  /// <li>output:"the-rain", "rain-in" ,"in-spain", "falls", "mainly"
	  /// </ul>
	  /// </summary>
//JAVA TO C# CONVERTER WARNING: Method 'throws' clauses are not available in .NET:
//ORIGINAL LINE: @Override public boolean incrementToken() throws java.io.IOException
	  public override bool incrementToken()
	  {
		while (!exhausted && input.incrementToken())
		{
		  State current = captureState();

		  if (previous != null && !GramType)
		  {
			restoreState(previous);
			previous = current;
			previousType = typeAttribute.type();

			if (GramType)
			{
			  posIncAttribute.PositionIncrement = 1;
			}
			return true;
		  }

		  previous = current;
		}

		exhausted = true;

		if (previous == null || GRAM_TYPE.Equals(previousType))
		{
		  return false;
		}

		restoreState(previous);
		previous = null;

		if (GramType)
		{
		  posIncAttribute.PositionIncrement = 1;
		}
		return true;
	  }

	  // ================================================= Helper Methods ================================================

	  /// <summary>
	  /// Convenience method to check if the current type is a gram type
	  /// </summary>
	  /// <returns> {@code true} if the current type is a gram type, {@code false} otherwise </returns>
	  public bool GramType
	  {
		  get
		  {
			return GRAM_TYPE.Equals(typeAttribute.type());
		  }
	  }
	}

}