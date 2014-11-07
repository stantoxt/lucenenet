﻿namespace org.apache.lucene.analysis.payloads
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


	using PayloadAttribute = org.apache.lucene.analysis.tokenattributes.PayloadAttribute;
	using TypeAttribute = org.apache.lucene.analysis.tokenattributes.TypeAttribute;
	using BytesRef = org.apache.lucene.util.BytesRef;


	/// <summary>
	/// Assigns a payload to a token based on the <seealso cref="org.apache.lucene.analysis.Token#type()"/>
	/// 
	/// 
	/// </summary>
	public class NumericPayloadTokenFilter : TokenFilter
	{

	  private string typeMatch;
	  private BytesRef thePayload;

	  private readonly PayloadAttribute payloadAtt = addAttribute(typeof(PayloadAttribute));
	  private readonly TypeAttribute typeAtt = addAttribute(typeof(TypeAttribute));

	  public NumericPayloadTokenFilter(TokenStream input, float payload, string typeMatch) : base(input)
	  {
		if (typeMatch == null)
		{
		  throw new System.ArgumentException("typeMatch cannot be null");
		}
		//Need to encode the payload
		thePayload = new BytesRef(PayloadHelper.encodeFloat(payload));
		this.typeMatch = typeMatch;
	  }

//JAVA TO C# CONVERTER WARNING: Method 'throws' clauses are not available in .NET:
//ORIGINAL LINE: @Override public final boolean incrementToken() throws java.io.IOException
	  public override bool incrementToken()
	  {
		if (input.incrementToken())
		{
		  if (typeAtt.type().Equals(typeMatch))
		  {
			payloadAtt.Payload = thePayload;
		  }
		  return true;
		}
		else
		{
		  return false;
		}
	  }
	}

}