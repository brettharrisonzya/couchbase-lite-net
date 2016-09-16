//
// ValidationsTest.cs
//
// Author:
//  Zachary Gramana  <zack@xamarin.com>
//
// Copyright (c) 2013, 2014 Xamarin Inc (http://www.xamarin.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
/*
* Original iOS version by Jens Alfke
* Ported to Android by Marty Schoch, Traun Leyden
*
* Copyright (c) 2012, 2013, 2014 Couchbase, Inc. All rights reserved.
*
* Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file
* except in compliance with the License. You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software distributed under the
* License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND,
* either express or implied. See the License for the specific language governing permissions
* and limitations under the License.
*/

using System.Collections.Generic;

using Couchbase.Lite.Internal;
using Couchbase.Lite.Util;
using NUnit.Framework;
using System;

namespace Couchbase.Lite
{
    [TestFixture("ForestDB")]
    public class ValidationsTest : LiteTestCase
    {
        public const string Tag = "Validations";

        public ValidationsTest(string storageType) : base(storageType) {}

        internal bool validationCalled = false;

        /// <exception cref="Couchbase.Lite.CouchbaseLiteException"></exception>
        [Test]
        public void TestValidations()
        {
            ValidateDelegate validator = (newRevision, context)=>
            {
                Assert.IsNotNull(newRevision);
                Assert.IsNotNull(context);
                Assert.IsTrue(newRevision.Properties != null || newRevision.IsDeletion);

                validationCalled = true;

                bool hoopy = newRevision.IsDeletion || (newRevision.Properties.Get("towel") != null);
                Console.WriteLine("--- Validating {0} --> {1}", newRevision.Properties, hoopy);
                if (!hoopy)
                {
                    context.Reject("Where's your towel?");
                }
                    
                return hoopy;
            };

            database.SetValidation("hoopy", validator);

            // POST a valid new document:
            IDictionary<string, object> props = new Dictionary<string, object>();
            props["name"] = "Zaphod Beeblebrox";
            props["towel"] = "velvet";
            RevisionInternal rev = new RevisionInternal(props);
            validationCalled = false;
            rev = database.PutRevision(rev, null, false);
            Assert.IsTrue(validationCalled);

            // PUT a valid update:
            props["head_count"] = 3;
            rev.SetProperties(props);
            validationCalled = false;
            rev = database.PutRevision(rev, rev.RevID, false);
            Assert.IsTrue(validationCalled);

            // PUT an invalid update:
            props.Remove("towel");
            rev.SetProperties(props);
            validationCalled = false;
            Assert.Throws<CouchbaseLiteException>(() => rev = database.PutRevision(rev, rev.RevID, false));
            Assert.IsTrue(validationCalled);

            // POST an invalid new document:
            props = new Dictionary<string, object>();
            props["name"] = "Vogon";
            props["poetry"] = true;
            rev = new RevisionInternal(props);
            validationCalled = false;
            Assert.Throws<CouchbaseLiteException>(() => database.PutRevision(rev, null, false));
            Assert.IsTrue(validationCalled);

            // PUT a valid new document with an ID:
            props = new Dictionary<string, object>();
            props["_id"] = "ford";
            props["name"] = "Ford Prefect";
            props["towel"] = "terrycloth";
            rev = new RevisionInternal(props);
            validationCalled = false;
            rev = database.PutRevision(rev, null, false);
            Assert.IsTrue(validationCalled);
            Assert.AreEqual("ford", rev.DocID);

            // DELETE a document:
            rev = new RevisionInternal(rev.DocID, rev.RevID, true);
            Assert.IsTrue(rev.Deleted);
            validationCalled = false;
            rev = database.PutRevision(rev, rev.RevID, false);
            Assert.IsTrue(validationCalled);

            // PUT an invalid new document:
            props = new Dictionary<string, object>();
            props["_id"] = "petunias";
            props["name"] = "Pot of Petunias";
            rev = new RevisionInternal(props);
            validationCalled = false;
            Assert.Throws<CouchbaseLiteException>(() => rev = database.PutRevision(rev, null, false));
            Assert.IsTrue(validationCalled);

            // Cancel the validation
            database.SetValidation("hoopy", null);
            validationCalled = false;
            Assert.DoesNotThrow(() => rev = database.PutRevision(rev, null, false));
            Assert.IsFalse(validationCalled);
        }
    }
}
