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

namespace Lucene.Net.Analysis.CharFilter
{
    /// <summary>
	/// Base utility class for implementing a <seealso cref="CharFilter"/>.
	/// You subclass this, and then record mappings by calling
	/// <seealso cref="#addOffCorrectMap"/>, and then invoke the correct
	/// method to correct an offset.
	/// </summary>
	public abstract class BaseCharFilter : CharFilter
	{

	  private int[] offsets;
	  private int[] diffs;
	  private int size = 0;

	  public BaseCharFilter(Reader @in) : base(@in)
	  {
	  }

	  /// <summary>
	  /// Retrieve the corrected offset. </summary>
	  protected internal override int correct(int currentOff)
	  {
		if (offsets == null || currentOff < offsets[0])
		{
		  return currentOff;
		}

		int hi = size - 1;
		if (currentOff >= offsets[hi])
		{
		  return currentOff + diffs[hi];
		}

		int lo = 0;
		int mid = -1;

		while (hi >= lo)
		{
		  mid = (int)((uint)(lo + hi) >> 1);
		  if (currentOff < offsets[mid])
		  {
			hi = mid - 1;
		  }
		  else if (currentOff > offsets[mid])
		  {
			lo = mid + 1;
		  }
		  else
		  {
			return currentOff + diffs[mid];
		  }
		}

		if (currentOff < offsets[mid])
		{
		  return mid == 0 ? currentOff : currentOff + diffs[mid - 1];
		}
		else
		{
		  return currentOff + diffs[mid];
		}
	  }

	  protected internal virtual int LastCumulativeDiff
	  {
		  get
		  {
			return offsets == null ? 0 : diffs[size-1];
		  }
	  }

	  /// <summary>
	  /// <para>
	  ///   Adds an offset correction mapping at the given output stream offset.
	  /// </para>
	  /// <para>
	  ///   Assumption: the offset given with each successive call to this method
	  ///   will not be smaller than the offset given at the previous invocation.
	  /// </para>
	  /// </summary>
	  /// <param name="off"> The output stream offset at which to apply the correction </param>
	  /// <param name="cumulativeDiff"> The input offset is given by adding this
	  ///                       to the output offset </param>
	  protected internal virtual void addOffCorrectMap(int off, int cumulativeDiff)
	  {
		if (offsets == null)
		{
		  offsets = new int[64];
		  diffs = new int[64];
		}
		else if (size == offsets.Length)
		{
		  offsets = ArrayUtil.grow(offsets);
		  diffs = ArrayUtil.grow(diffs);
		}

		assert(size == 0 || off >= offsets[size - 1]) : "Offset #" + size + "(" + off + ") is less than the last recorded offset " + offsets[size - 1] + "\n" + Arrays.ToString(offsets) + "\n" + Arrays.ToString(diffs);

		if (size == 0 || off != offsets[size - 1])
		{
		  offsets[size] = off;
		  diffs[size++] = cumulativeDiff;
		} // Overwrite the diff at the last recorded offset
		else
		{
		  diffs[size - 1] = cumulativeDiff;
		}
	  }
	}

}