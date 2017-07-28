﻿//
// DatabaseTest.cs
//
// Author:
//  Pasin Suriyentrakorn <pasin@couchbase.com>
//
// Copyright (c) 2014 Xamarin Inc
// Copyright (c) 2014 .NET Foundation
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
//
// Copyright (c) 2014 Couchbase, Inc. All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file
// except in compliance with the License. You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software distributed under the
// License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND,
// either express or implied. See the License for the specific language governing permissions
// and limitations under the License.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Couchbase.Lite.Internal;
using Couchbase.Lite.Store;
using Couchbase.Lite.Util;
using FluentAssertions;
using NUnit.Framework;
using Couchbase.Lite.Storage.SQLCipher;
using System.Text;
using Couchbase.Lite.Revisions;

namespace Couchbase.Lite
{
    [TestFixture("ForestDB")]
    public class DatabaseTest : LiteTestCase
    {
        const String TooLongName = "a11111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111110";

        public DatabaseTest(string storageType) : base(storageType) {}

        [Test]
        public void TestAutoPruneKeepsConflictParent()
        {
            var doc = database.CreateDocument();
            var rev = doc.CreateRevision();
            rev.SetUserProperties(new Dictionary<string, object> {
                ["test"] = true
            });

            var saved = rev.Save();
            var rev2a = saved.CreateRevision();
            var rev2b = saved.CreateRevision();

            rev2a.SetUserProperties(new Dictionary<string, object> {
                ["test"] = true,
                ["version"] = 1
            });
            saved = rev2a.Save();

            rev2b.SetUserProperties(new Dictionary<string, object> {
                ["foo"] = "bar"
            });
            var conflict = rev2b.Save(true);

            database.RunInTransaction(() => {
                for(int i = 0; i < 30; i++) {
                    var newRev = saved.CreateRevision();
                    newRev.SetUserProperties(new Dictionary<string, object> {
                        ["test"] = true,
                        ["version"] = i + 2
                    });
                    saved = newRev.Save();
                }

                return true;
            });

            var gotDoc = database.GetDocument(doc.Id).ConflictingRevisions.Where(x => x.Id == conflict.Id).FirstOrDefault();
            gotDoc.Should().NotBeNull("because the conflict should still exist");
            gotDoc.Parent.Should().NotBeNull("because at least one parent should exist for a non-root revision");
        }


        [Test]
        public void TestQueryIndependence()
        {
            if(_storageType != "SQLite") {
                return;
            }

            CreateDocuments (database, 10);

            var engine = (database.Storage as SqliteCouchStore).StorageEngine;
            var are = new AutoResetEvent (false);
            Task.Factory.StartNew (async () => {
                var c = engine.RawQuery ("SELECT * FROM revs");
                c.MoveToNext ();
                are.Set ();
                await Task.Delay (3000);
                c.Close ();
            });

            are.WaitOne ();
            database.GetDocument ("ghost").PutProperties (new Dictionary<string, object> {
                ["line"] = "boo"
            });

            Assert.IsNotNull (database.GetExistingDocument ("ghost"));
        }

        [Test]
        public void TestRollbackInvalidatesCache()
        {
            var props = new Dictionary<string, object> {
                ["exists"] = false
            };

            database.RunInTransaction (() => {
                database.GetDocument ("rogue").PutProperties (props);
                return false; // Cancel the transaction
            });

            props ["exists"] = true;
            var rev = database.GetDocument ("proper").PutProperties (props);

            Assert.IsNull (database.GetExistingDocument ("rogue"));
            if(_storageType == StorageEngineTypes.SQLite) {
                Assert.AreEqual(1, rev.Sequence);
            }

            rev = database.GetDocument ("rogue").PutProperties (props);
            if(_storageType == StorageEngineTypes.SQLite) {
                Assert.AreEqual(2, rev.Sequence);
            }
        }

        [Test]
        public void TestFindMissingRevisions()
        {
            var revs = new RevisionList();
            database.Storage.FindMissingRevisions(revs);

            var doc1r1 = PutDoc(new Dictionary<string, object> {
                ["_id"] = "11111",
                ["key"] = "one"
            });
            var doc2r1 = PutDoc(new Dictionary<string, object> {
                ["_id"] = "22222",
                ["key"] = "two"
            });
            PutDoc(new Dictionary<string, object> {
                ["_id"] = "33333",
                ["key"] = "three"
            });
            PutDoc(new Dictionary<string, object> {
                ["_id"] = "44444",
                ["key"] = "four"
            });
            PutDoc(new Dictionary<string, object> {
                ["_id"] = "55555",
                ["key"] = "five"
            });

            var doc1r2 = PutDoc(new Dictionary<string, object> {
                ["_id"] = "11111",
                ["_rev"] = doc1r1.RevID.ToString(),
                ["key"] = "one+"
            });
            var doc2r2 = PutDoc(new Dictionary<string, object> {
                ["_id"] = "22222",
                ["_rev"] = doc2r1.RevID.ToString(),
                ["key"] = "two+"
            });

            PutDoc(new Dictionary<string, object> {
                ["_id"] = "11111",
                ["_rev"] = doc1r2.RevID.ToString(),
                ["_deleted"] = true
            });

            // Now call FindMissingRevisions
            var revToFind1 = new RevisionInternal("11111", "3-6060".AsRevID(), false);
            var revToFind2 = new RevisionInternal("22222", doc2r2.RevID, false);
            var revToFind3 = new RevisionInternal("99999", "9-4141".AsRevID(), false);
            revs = new RevisionList(new List<RevisionInternal> { revToFind1, revToFind2, revToFind3 });
            database.Storage.FindMissingRevisions(revs);
            CollectionAssert.AreEqual(new List<RevisionInternal> { revToFind1, revToFind3 }, revs);

            // Check the possible ancestors
            ValueTypePtr<bool> haveBodies = false;
            CollectionAssert.AreEqual(new List<RevisionID> { doc1r2.RevID, doc1r1.RevID }, database.Storage.GetPossibleAncestors(revToFind1, 0, haveBodies));
            CollectionAssert.AreEqual(new List<RevisionID> { doc1r2.RevID }, database.Storage.GetPossibleAncestors(revToFind1, 1, haveBodies));
            CollectionAssert.AreEqual(new List<RevisionID>(), database.Storage.GetPossibleAncestors(revToFind3, 0, haveBodies));
        }

        [Test]
        public void TestPruneOnPut()
        {
            database.SetMaxRevTreeDepth(5);
            var lastRev = default(RevisionInternal);
            var revs = new List<RevisionInternal>();
            for(int gen = 1; gen <= 10; gen++) {
                var newRev = new RevisionInternal(new Dictionary<string, object> {
                    { "_id", "foo" },
                    { "gen", gen }
                });

                var rev = database.PutRevision(newRev, lastRev?.RevID, false);
                revs.Add(rev);
                lastRev = rev;
            }

            // Verify that the first five revs are no longer available:
            for(int gen = 1; gen <= 10; gen++) {
                var rev = database.GetDocument("foo", revs[gen - 1].RevID, true);
                if(gen <= 5) {
                    Assert.IsNull(rev);
                } else {
                    Assert.IsNotNull(rev);
                }
            }
        }

        [Test]
        public void TestPruneOnForceInsert()
        {
            database.SetMaxRevTreeDepth(5);
            var lastRev = default(RevisionInternal);
            var revs = new List<RevisionInternal>();
            var history = new List<RevisionID>();
            for(int gen = 1; gen <= 10; gen++) {
                var rev = new RevisionInternal(new Dictionary<string, object> {
                    { "_id", "foo" },
                    { "_rev", $"{gen}-cafebabe" },
                    { "gen", gen }
                });

                database.ForceInsert(rev, history, null);
                history.Insert(0, rev.RevID);
                revs.Add(rev);
                lastRev = rev;
            }

            // Verify that the first five revs are no longer available:
            for(int gen = 1; gen <= 10; gen++) {
                var rev = database.GetDocument("foo", revs[gen - 1].RevID, true);
                if(gen <= 5) {
                    Assert.IsNull(rev);
                } else {
                    Assert.IsNotNull(rev);
                }
            }
        }

        [Test]
        public void TestAttachments()
        {
            var properties = new Dictionary<string, object> {
                { "testName", "testAttachments" }
            };
            var doc = CreateDocumentWithProperties(database, properties);
            var rev = doc.CurrentRevision;

            Assert.AreEqual(0, rev.Attachments.Count());
            Assert.AreEqual(0, rev.AttachmentNames.Count());
            using(var a = rev.GetAttachment("index.html")) { 
                Assert.IsNull(a);
            }

            var body = Encoding.UTF8.GetBytes("This is a test attachment!");
            var rev2 = doc.CreateRevision();
            rev2.SetAttachment("index.html", "text/plain; charset=utf-8", body);

            Assert.AreEqual(1, rev2.Attachments.Count());
            CollectionAssert.AreEqual(new string[] { "index.html" }, rev2.AttachmentNames);
            var rev2Attach = rev2.GetAttachment("index.html");
            Assert.IsNull(rev2Attach.Revision);
            Assert.IsNull(rev2Attach.Document);
            Assert.AreEqual("index.html", rev2Attach.Name);
            Assert.AreEqual("text/plain; charset=utf-8", rev2Attach.ContentType);
            Assert.AreEqual(body, rev2Attach.Content);
            Assert.AreEqual(body.Length, rev2Attach.Length);

            var rev3 = rev2.Save();
            rev2.Dispose();
            Assert.AreEqual(1, rev3.Attachments.Count());
            Assert.AreEqual(1, rev3.AttachmentNames.Count());

            using(var attach = rev3.GetAttachment("index.html")) {
                Assert.AreEqual(doc, attach.Document);
                Assert.AreEqual("index.html", attach.Name);
                CollectionAssert.AreEqual(new string[] { "index.html" }, rev3.AttachmentNames);

                Assert.AreEqual("text/plain; charset=utf-8", attach.ContentType);
                Assert.AreEqual(body, attach.Content);
                Assert.AreEqual(body.Length, attach.Length);

                var inStream = attach.ContentStream;
                var data = inStream.ReadAllBytes();
                Assert.AreEqual(body, data);

                var newRev = rev3.CreateRevision();
                newRev.RemoveAttachment(attach.Name);
                var rev4 = newRev.Save();
                newRev.Dispose();
                Assert.AreEqual(0, rev4.AttachmentNames.Count());
            }
            
            // Add an attachment with revpos=0 (see #627)
            var props = rev3.Properties;
            var atts = props.Get("_attachments").AsDictionary<string, object>();
            atts["zero.txt"] = new Dictionary<string, object> {
                { "content_type", "text/plain" },
                { "revpos", 0 },
                { "following", true }
            };

            props["_attachments"] = atts;
            var success = doc.PutExistingRevision(props, new Dictionary<string, Stream> {
                { "zero.txt", new MemoryStream(Encoding.UTF8.GetBytes("zero")) }
            }, new List<string> { "3-0000", rev3.Id, rev.Id }, null);
            Assert.IsTrue(success);

            var rev5 = doc.GetRevision("3-0000");
            using(var att = rev5.GetAttachment("zero.txt")) {
                Assert.IsNotNull(att);
            }
        }

        [Test]
        public void TestAllDocumentsPrefixMatch()
        {
            CreateDocumentWithProperties(database, new Dictionary<string, object> { { "_id", "three" } });
            CreateDocumentWithProperties(database, new Dictionary<string, object> { { "_id", "four" } });
            CreateDocumentWithProperties(database, new Dictionary<string, object> { { "_id", "five" } });
            CreateDocumentWithProperties(database, new Dictionary<string, object> { { "_id", "eight" } });
            CreateDocumentWithProperties(database, new Dictionary<string, object> { { "_id", "fifteen" } });

            database.DocumentCache.Clear();

            var query = database.CreateAllDocumentsQuery();
            var rows = default(QueryEnumerator);

            // Set prefixMatchLevel = 1, no startKey, ascending:
            query.Descending = false;
            query.EndKey = "f";
            query.PrefixMatchLevel = 1;
            rows = query.Run();
            Assert.AreEqual(4, rows.Count);
            CollectionAssert.AreEqual(new[] { "eight", "fifteen", "five", "four" }, rows.Select(x => x.Key));

            // Set prefixMatchLevel = 1, ascending:
            query.Descending = false;
            query.StartKey = "f";
            query.EndKey = "f";
            query.PrefixMatchLevel = 1;
            rows = query.Run();
            Assert.AreEqual(3, rows.Count);
            CollectionAssert.AreEqual(new[] { "fifteen", "five", "four" }, rows.Select(x => x.Key));

            // Set prefixMatchLevel = 1, descending:
            query.Descending = true;
            query.StartKey = "f";
            query.EndKey = "f";
            query.PrefixMatchLevel = 1;
            rows = query.Run();
            Assert.AreEqual(3, rows.Count);
            CollectionAssert.AreEqual(new[] { "four", "five", "fifteen" }, rows.Select(x => x.Key));

            // Set prefixMatchLevel = 1, ascending, prefix = fi:
            query.Descending = false;
            query.StartKey = "fi";
            query.EndKey = "fi";
            query.PrefixMatchLevel = 1;
            rows = query.Run();
            Assert.AreEqual(2, rows.Count);
            CollectionAssert.AreEqual(new[] { "fifteen", "five" }, rows.Select(x => x.Key));

        }

        #if !NET_3_5
        [Test]
        public void TestParallelLibrary()
        {
            const int docCount = 200;

            Parallel.Invoke(() => {
                Parallel.For(0, docCount, i =>
                {
                    Assert.DoesNotThrow(() => database.GetExistingDocument(i.ToString()));
                });
            }, () => {
                Parallel.For(0, docCount, i =>
                {
                    Assert.DoesNotThrow(() => database.GetExistingDocument(i.ToString()));
                });
            });
        }

        #endif

        [Test]
        public void TestReadOnlyDb()
        {
            CreateDocuments(database, 10);
            database.Close();

            var options = new ManagerOptions();
            options.ReadOnly = true;
            var readOnlyManager = new Manager(new DirectoryInfo(manager.Directory), options);
            database = readOnlyManager.GetExistingDatabase(database.Name);
            Assert.IsNotNull(database);
            var e = Assert.Throws<CouchbaseLiteException>(() => CreateDocuments(database, 1));
            Assert.AreEqual(StatusCode.Forbidden, e.Code);
            database.Close();

            var dbOptions = new DatabaseOptions();
            dbOptions.ReadOnly = true;
            database = manager.OpenDatabase(database.Name, dbOptions);
            Assert.IsNotNull(database);
            e = Assert.Throws<CouchbaseLiteException>(() => CreateDocuments(database, 1));
            Assert.AreEqual(StatusCode.Forbidden, e.Code);
            database.Close();

            dbOptions.ReadOnly = false;
            database = manager.OpenDatabase(database.Name, dbOptions);
            Assert.DoesNotThrow(() => CreateDocuments(database, 1));
        }

        [Test]
        public void TestUpgradeDatabase()
        {
            // Install a canned database:
            using (var dbStream = GetAsset("ios120.zip")) {
                Assert.DoesNotThrow(() => manager.ReplaceDatabase("replacedb", dbStream, true));
            }

            // Open installed db with storageType set to this test's storage type:
            var options = new DatabaseOptions();
            options.StorageType = _storageType;
            var replacedb = default(Database);
            Assert.DoesNotThrow(() => replacedb = manager.OpenDatabase("replacedb", options));
            Assert.IsNotNull(replacedb);

            // Verify storage type matches what we requested:
            Assert.IsInstanceOf(database.Storage.GetType(), replacedb.Storage);

            // Test db contents:
            CheckRowsOfReplacedDB("replacedb", rows =>
            {
                Assert.AreEqual(1, rows.Count);
                var doc = rows.ElementAt(0).Document;
                Assert.AreEqual("doc1", doc.Id);
                Assert.AreEqual(2, doc.CurrentRevision.Attachments.Count());
                var att1 = doc.CurrentRevision.GetAttachment("attach1");
                Assert.IsNotNull(att1);
                Assert.AreEqual(att1.Length, att1.Content.Count());

                var att2 = doc.CurrentRevision.GetAttachment("attach2");
                Assert.IsNotNull(att2);
                Assert.AreEqual(att2.Length, att2.Content.Count());
            });

            // Close and re-open the db using SQLite storage type. Should fail if it used to be ForestDB:
            Assert.DoesNotThrow(() => replacedb.Close().Wait(15000));
            options.StorageType = StorageEngineTypes.SQLite;
            if (_storageType == StorageEngineTypes.SQLite) {
                Assert.DoesNotThrow(() => replacedb = manager.OpenDatabase("replacedb", options));
                Assert.IsNotNull(replacedb);
            } else {
                var e = Assert.Throws<CouchbaseLiteException>(() => replacedb = manager.OpenDatabase("replacedb", options));
                Assert.AreEqual(StatusCode.InvalidStorageType, e.Code);
            }
        }

        [Test]
        public void TestValidDatabaseNames([Values("foo", "try1", "foo-bar", "goofball99", TooLongName)] String testName)
        {
            // Arrange.
            // Act.
            if (testName.Length == 240) {
                testName = testName.Trim('0');
            }
            var result = Manager.IsValidDatabaseName(testName);

            // Assert.
            Assert.IsTrue(result);
        }

        [Test]
        public void TestInvalidDatabaseNames([Values("Foo", "1database", "", "foo;", TooLongName)] String testName)
        {
            // Arrange.
            // Act.
            var result = Manager.IsValidDatabaseName(testName);

            // Assert.
            Assert.IsFalse(result);
        }

        [Test]
        public void TestGetDatabaseNameFromPath() 
        {
            Assert.AreEqual("baz", FileDirUtils.GetDatabaseNameFromPath("foo/bar/baz.cblite"));
        }

        [Test]
        public void TestPruneRevsToMaxDepthViaCompact()
        {
            var properties = new Dictionary<string, object>();
            properties.Add("testName", "testDatabaseCompaction");
            properties.Add("tag", 1337);

            var doc = CreateDocumentWithProperties(database, properties);
            var rev = doc.CurrentRevision;
            database.SetMaxRevTreeDepth(1);

            for (int i = 0; i < 10; i++)
            {
                var properties2 = new Dictionary<string, object>(properties);
                properties2["tag"] = i;
                rev = rev.CreateRevision(properties2);
            }

            database.Compact();

            var fetchedDoc = database.GetDocument(doc.Id);
            var revisions = fetchedDoc.RevisionHistory.ToList();
            Assert.AreEqual(1, revisions.Count);
        }

        /// <summary>
        /// When making inserts in a transaction, the change notifications should
        /// be batched into a single change notification (rather than a change notification
        /// for each insert)
        /// </summary>
        /// <exception cref="System.Exception"></exception>
        [Test]
        public void TestChangeListenerNotificationBatching()
        {
            const int numDocs = 50;
            var doneSignal = new CountdownEvent(1);

            database.Changed += (sender, e) => doneSignal.Signal(); ;

            database.RunInTransaction(() =>
            {
                CreateDocuments(database, numDocs);
                
                return true;
            });

            var success = doneSignal.Wait(TimeSpan.FromSeconds(1));
            Assert.IsTrue(success);
        }

        /// <summary>
        /// When making inserts outside of a transaction, there should be a change notification
        /// for each insert (no batching)
        /// </summary>
        [Test]
        public void TestChangeListenerNotification()
        {
            const int numDocs = 50;
            var countdownEvent = new CountdownEvent(numDocs);

            database.Changed += (sender, e) =>
            {
                countdownEvent.Signal(e.Changes.Count());
            };
            CreateDocuments(database, numDocs);
            Assert.IsTrue(countdownEvent.Wait(TimeSpan.FromSeconds(1)));
        }

        /// <summary>
        /// When making inserts outside of a transaction, there should be a change notification
        /// for each insert (no batching)
        /// </summary>
        [Test]
        public void TestGetActiveReplications()
        {
            if (!Boolean.Parse((string)GetProperty("replicationTestsEnabled")))
            {
                Assert.Inconclusive("Replication tests disabled.");
                return;
            }

            var remote = GetReplicationURL();
            var doneSignal = new ManualResetEvent(false);
            var replication = database.CreatePullReplication(remote);
            replication.Continuous = true;

            Func<Replication, bool> doneLogic = r =>
                replication.Status == ReplicationStatus.Active;
            
            replication.Changed += (sender, e) => {
                if (doneLogic(e.Source)) {
                    doneSignal.Set();
                }
            };
                
            Assert.AreEqual(0, database.AllReplications.ToList().Count);

            replication.Start();
            var passed = doneSignal.WaitOne(TimeSpan.FromSeconds(5));
            Assert.IsTrue(passed);
            Assert.AreEqual(1, database.AllReplications.Count());
        }

        [Test]
        public void TestEncodeDocumentJSON() 
        {
            var sqliteStorage = database.Storage as SqliteCouchStore;
            if (sqliteStorage == null) {
                Assert.Inconclusive("This test is only valid on an SQLite store");
            }

            var props = new Dictionary<string, object>() 
            {
                {"_local_seq", ""}
            };

            var revisionInternal = new RevisionInternal(props);
            var encoded = sqliteStorage.EncodeDocumentJSON(revisionInternal);
            Assert.IsNotNull(encoded);
        }

        [Test]
        public void TestWinningRevIDOfDoc()
        {
            var sqliteStorage = database.Storage as SqliteCouchStore;
            if (sqliteStorage == null) {
                Assert.Inconclusive("This test is only valid on an SQLite store");
            }

            var properties = new Dictionary<string, object>() 
            {
                {"testName", "testCreateRevisions"},
                {"tag", 1337}
            };

            var properties2a = new Dictionary<string, object>() 
            {
                {"testName", "testCreateRevisions"},
                {"tag", 1338}
            };

            var properties2b = new Dictionary<string, object>()
            {
                {"testName", "testCreateRevisions"},
                {"tag", 1339}
            };

            var doc = database.CreateDocument();
            var newRev1 = doc.CreateRevision();
            newRev1.SetUserProperties(properties);
            var rev1 = newRev1.Save();

            ValueTypePtr<bool> outIsDeleted = false;
            ValueTypePtr<bool> outIsConflict = false;

            var docNumericId = sqliteStorage.GetDocNumericID(doc.Id);
            Assert.IsTrue(docNumericId != 0);
            Assert.AreEqual(rev1.Id.AsRevID(), sqliteStorage.GetWinner(docNumericId, outIsDeleted, outIsConflict));
            Assert.IsFalse(outIsConflict);

            var newRev2a = rev1.CreateRevision();
            newRev2a.SetUserProperties(properties2a);
            var rev2a = newRev2a.Save();
            Assert.AreEqual(rev2a.Id.AsRevID(), sqliteStorage.GetWinner(docNumericId, outIsDeleted, outIsConflict));
            Assert.IsFalse(outIsConflict);

            var newRev2b = rev1.CreateRevision();
            newRev2b.SetUserProperties(properties2b);
            newRev2b.Save(true);
            sqliteStorage.GetWinner(docNumericId, outIsDeleted, outIsConflict);
            Assert.IsTrue(outIsConflict);
        }

        private void CheckRowsOfReplacedDB(string dbName, Action<QueryEnumerator> onComplete)
        {
            var replacedb = default(Database);
            Assert.DoesNotThrow(() => replacedb = manager.OpenDatabase(dbName, null));
            Assert.IsNotNull(replacedb);

            var view = replacedb.GetView("myview");
            Assert.IsNotNull(view);
            view.SetMap((doc, emit) =>
            {
                emit(doc.CblID(), null);
            }, "1.0");

            var query = view.CreateQuery();
            Assert.IsNotNull(query);
            query.Prefetch = true;
            var rows = default(QueryEnumerator);
            Assert.DoesNotThrow(() => rows = query.Run());
            onComplete(rows);
        }

        private RevisionInternal PutDoc(IDictionary<string, object> props)
        {
            var rev = new RevisionInternal(props);
            var result = database.PutRevision(rev, props.CblRev(), false);
            Assert.IsNotNull(result.RevID);
            return result;
        }
    }
}

