//
// Database.cs
//
// Author:
//     Zachary Gramana  <zack@xamarin.com>
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
using System.Text;
using System.Threading.Tasks;

using Couchbase.Lite.Internal;
using Couchbase.Lite.Replicator;
using Couchbase.Lite.Store;
using Couchbase.Lite.Support;
using Couchbase.Lite.Util;
using Sharpen;
using System.Collections.Concurrent;
using Couchbase.Lite.Db;
using System.Threading;


#if !NET_3_5
using System.Net;
using StringEx = System.String;
#else
using System.Net.Couchbase;
#endif

namespace Couchbase.Lite 
{

    /// <summary>
    /// A Couchbase Lite Database.
    /// </summary>
    public sealed class Database : ICouchStoreDelegate, IDisposable
    {
        #region Constants

        internal const string TAG = "Database";
        internal const string TAG_SQL = "CBLSQL";
        internal const int BIG_ATTACHMENT_LENGTH = 2 * 1024;

        private const bool AUTO_COMPACT = true;
        private const int NOTIFY_CHANGES_LIMIT = 5000;
        private const int MAX_DOC_CACHE_SIZE = 50;
        private const int DEFAULT_MAX_REVS = 20;
        private const string LOCAL_CHECKPOINT_DOC_ID = "CBL_LocalCheckpoint";
        private const string CHECKPOINT_LOCAL_UUID_KEY = "localUUID";

        private static readonly HashSet<string> SPECIAL_KEYS_TO_REMOVE = new HashSet<string> {
            "_id", "_rev", "_deleted", "_revisions", "_revs_info", "_conflicts", "_deleted_conflicts",
            "_local_seq"
        };

        private static readonly HashSet<string> SPECIAL_KEYS_TO_LEAVE = new HashSet<string> {
            "_removed", "_attachments"
        };

        #endregion

        #region Variables

        private CookieStore                             _persistentCookieStore;

        private IDictionary<string, BlobStoreWriter>    _pendingAttachmentsByDigest;
        private IDictionary<string, View>               _views;
        private IList<DocumentChange>                   _changesToNotify;
        private bool                                    _isPostingChangeNotifications;
        private object                                  _allReplicatorsLocker = new object();
        private bool                                    _readonly;
        private Task                                    _closingTask;

        #endregion

        #region Properties

        internal bool IsOpen { get; private set; }

        /// <summary>
        /// Gets or sets an object that can compile source code into <see cref="FilterDelegate"/>.
        /// </summary>
        /// <value>The filter compiler object.</value>
        public static IFilterCompiler FilterCompiler { get; set; }

        /// <summary>
        /// Gets the container the holds cookie information received from the remote replicator
        /// </summary>
        [Obsolete("This will be removed in a future version.  Replicators now have their own cookie stores")]
        public CookieContainer PersistentCookieStore
        {
            get {
                if (!IsOpen) {
                    Log.W(TAG, "PersistentCookeStore called on closed database");
                    return null;
                }

                if (_persistentCookieStore == null) {
                    _persistentCookieStore = new CookieStore(this, "Shared");
                }

                return _persistentCookieStore;
            }
        }

        /// <summary>
        /// Gets the <see cref="Couchbase.Lite.Database"/> name.
        /// </summary>
        /// <value>The database name.</value>
        public string Name { get; internal set; }

        /// <summary>
        /// Gets the <see cref="Couchbase.Lite.Manager" /> that owns this <see cref="Couchbase.Lite.Database"/>.
        /// </summary>
        /// <value>The manager object.</value>
        public Manager Manager { get; private set; }

        /// <summary>
        /// Gets the number of <see cref="Couchbase.Lite.Document" /> in the <see cref="Couchbase.Lite.Database"/>.
        /// </summary>
        /// <value>The document count.</value>
        [Obsolete("This property is heavy and will be converted to a method")]
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public int DocumentCount 
        {
            get {
                return GetDocumentCount();
            }
        }

        /// <summary>
        /// Gets the latest sequence number used by the <see cref="Couchbase.Lite.Database" />.  Every new <see cref="Couchbase.Lite.Revision" /> is assigned a new sequence 
        /// number, so this property increases monotonically as changes are made to the <see cref="Couchbase.Lite.Database" />. This can be used to 
        /// check whether the <see cref="Couchbase.Lite.Database" /> has changed between two points in time.
        /// </summary>
        /// <value>The last sequence number.</value>
        [Obsolete("This property is heavy and will be converted to a method")]
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public long LastSequenceNumber 
        {
            get {
                return GetLastSequenceNumber();
            }
        }

        /// <summary>
        /// Gets the total size of the database on the filesystem.
        /// </summary>
        [Obsolete("This property is heavy and will be converted to a method")]
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public long TotalDataSize {
            get {
                return GetTotalDataSize();
            }
        }

        /// <summary>
        /// Gets all the running <see cref="Couchbase.Lite.Replication" />s 
        /// for this <see cref="Couchbase.Lite.Database" />.  
        /// This includes all continuous <see cref="Couchbase.Lite.Replication" />s and 
        /// any non-continuous <see cref="Couchbase.Lite.Replication" />s that has been started 
        /// and are still running.
        /// </summary>
        /// <value>All replications.</value>
        public IEnumerable<Replication> AllReplications 
        { 
            get { 
                if (!IsOpen) {
                    Log.W(TAG, "AllReplications called on closed database");
                    return null;
                }

                return AllReplicators.ToList(); 
            } 
        }

        /// <summary>
        /// Maximum depth of a document's revision tree (or, max length of its revision history.)
        /// Revisions older than this limit will be deleted during a -compact: operation.
        /// </summary>
        /// <remarks>
        /// Maximum depth of a document's revision tree (or, max length of its revision history.)
        /// Revisions older than this limit will be deleted during a -compact: operation.
        /// Smaller values save space, at the expense of making document conflicts somewhat more likely.
        /// </remarks>
        [Obsolete("This property is heavy and will be converted to a method")]
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public int MaxRevTreeDepth 
        {
            get { return GetMaxRevTreeDepth(); }
            set { SetMaxRevTreeDepth(value); }
        }
        private int _maxRevTreeDepth;

        internal ICouchStore Storage { get; private set; }

        internal SharedState Shared { 
            get {
                return Manager.Shared;
            }
        }

        internal String                                 DbDirectory { get; private set; }
        internal IList<Replication>                     ActiveReplicators { get; set; }
        internal IList<Replication>                     AllReplicators { get; set; }
        internal LruCache<String, Document>             DocumentCache { get; set; }
        internal ConcurrentDictionary<String, WeakReference>     UnsavedRevisionDocumentCache { get; set; }
        internal long StartTime { get; private set; }
        internal BlobStoreWriter AttachmentWriter { get { return new BlobStoreWriter(Attachments); } }

        internal BlobStore Attachments { get; set; }


        private IDictionary<String, FilterDelegate>     Filters { get; set; }
        private TaskFactory Scheduler { get; set; }

        private IDictionary<string, BlobStoreWriter> PendingAttachmentsByDigest
        {
            get {
                return _pendingAttachmentsByDigest ?? (_pendingAttachmentsByDigest = new Dictionary<string, BlobStoreWriter>());
            }
            set {
                _pendingAttachmentsByDigest = value;
            }
        }

        #endregion

        #region Constructors

        // "_local/*" is not a valid document ID. Local docs have their own API and shouldn't get here.
        internal static String GenerateDocumentId()
        {
            return Misc.CreateGUID();
        }

        internal Database(string directory, string name, Manager manager, bool readOnly)
        {
            Debug.Assert(Path.IsPathRooted(directory));

            //path must be absolute
            DbDirectory = directory;
            Name = name ?? FileDirUtils.GetDatabaseNameFromPath(DbDirectory);
            Manager = manager;
            DocumentCache = new LruCache<string, Document>(MAX_DOC_CACHE_SIZE);
            UnsavedRevisionDocumentCache = new ConcurrentDictionary<string, WeakReference>();
            _readonly = readOnly;
 
            // FIXME: Not portable to WinRT/WP8.
            ActiveReplicators = new List<Replication>();
            AllReplicators = new List<Replication> ();

            _changesToNotify = new List<DocumentChange>();
            Scheduler = new TaskFactory(new SingleTaskThreadpoolScheduler());
            StartTime = DateTime.UtcNow.ToMillisecondsSinceEpoch ();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Gets the number of <see cref="Couchbase.Lite.Document" /> in the <see cref="Couchbase.Lite.Database"/>.
        /// </summary>
        /// <returns>The document count.</returns>
        public int GetDocumentCount()
        {
            if (!IsOpen) {
                Log.W(TAG, "GetDocumentCount called on closed database");
                return 0;
            }

            return Storage.DocumentCount;
        }

        /// <summary>
        /// Gets the latest sequence number used by the <see cref="Couchbase.Lite.Database" />.  Every new <see cref="Couchbase.Lite.Revision" /> is assigned a new sequence 
        /// number, so this property increases monotonically as changes are made to the <see cref="Couchbase.Lite.Database" />. This can be used to 
        /// check whether the <see cref="Couchbase.Lite.Database" /> has changed between two points in time.
        /// </summary>
        /// <returns>The last sequence number.</returns>
        public long GetLastSequenceNumber()
        {
            if (!IsOpen) {
                Log.W(TAG, "GetLastSequenceNumber called on closed database");
                return 0;
            }

            return Storage.LastSequence;
        }

        /// <summary>
        /// Gets the total size of the database on the filesystem.
        /// </summary>
        /// <returns>The total size of the database on the filesystem.</returns>
        public long GetTotalDataSize()
        {
            if (!IsOpen) {
                Log.W(TAG, "TotalDataSize called on closed database");
                return 0L;
            }

            string dir = DbDirectory;
            var info = new DirectoryInfo(dir);

            // Database files
            return info.EnumerateFiles("*", SearchOption.AllDirectories).Sum(x => x.Length);
        }

        /// <summary>
        /// Maximum depth of a document's revision tree (or, max length of its revision history.)
        /// Revisions older than this limit will be deleted during a -compact: operation.
        /// </summary>
        /// <remarks>
        /// Maximum depth of a document's revision tree (or, max length of its revision history.)
        /// Revisions older than this limit will be deleted during a -compact: operation.
        /// Smaller values save space, at the expense of making document conflicts somewhat more likely.
        /// </remarks>
        /// <returns>The maximum depth set on this Database</returns>
        public int GetMaxRevTreeDepth()
        {
            return Storage != null ? Storage.MaxRevTreeDepth : _maxRevTreeDepth;
        }

        /// <summary>
        /// Sets the maximum depth of a document's revision tree (or, max length of its revision history.)
        /// Revisions older than this limit will be deleted during a -compact: operation.
        /// </summary>
        /// <remarks>
        /// Maximum depth of a document's revision tree (or, max length of its revision history.)
        /// Revisions older than this limit will be deleted during a -compact: operation.
        /// Smaller values save space, at the expense of making document conflicts somewhat more likely.
        /// </remarks>
        /// <param name="value">The new maximum depth to use for this Database</param> 
        public void SetMaxRevTreeDepth(int value)
        {
            if (value == 0) {
                value = DEFAULT_MAX_REVS;
            }

            _maxRevTreeDepth = value;
            if (Storage != null && value != Storage.MaxRevTreeDepth) {
                var last = Storage.MaxRevTreeDepth;
                Storage.MaxRevTreeDepth = value;
                if (last == 0) {
                    var saved = Storage.GetInfo("max_revs");
                    var savedInt = 0;
                    if (saved != null && Int32.TryParse(saved, out savedInt) && savedInt == value) {
                        return;
                    }

                    Storage.SetInfo("max_revs", value.ToString());
                }
            }
        }

        /// <summary>
        /// Compacts the <see cref="Couchbase.Lite.Database" /> file by purging non-current 
        /// <see cref="Couchbase.Lite.Revision" />s and deleting unused <see cref="Couchbase.Lite.Attachment" />s.
        /// </summary>
        /// <exception cref="Couchbase.Lite.CouchbaseLiteException">thrown if an issue occurs while 
        /// compacting the <see cref="Couchbase.Lite.Database" /></exception>
        public bool Compact()
        {
            if (!IsOpen) {
                Log.W(TAG, "CreatePushReplication called on closed database");
                return false;
            }

            try {
                Storage.Compact();
            } catch(CouchbaseLiteException) {
                Log.W(TAG, "Error during compaction");
                throw;
            } catch(Exception e) {
                throw new CouchbaseLiteException("Error during compaction", e) { Code = StatusCode.Exception };
            }

            return GarbageCollectAttachments();
        }

        /// <summary>
        /// Deletes the <see cref="Couchbase.Lite.Database" />.
        /// </summary>
        /// <exception cref="Couchbase.Lite.CouchbaseLiteException">
        /// Thrown if an issue occurs while deleting the <see cref="Couchbase.Lite.Database" /></exception>
        public void Delete()
        {
            Close().Wait();
            if (!Exists()) {
                return;
            }

            Directory.Delete(DbDirectory, true);
        }

        /// <summary>
        /// Gets or creates the <see cref="Couchbase.Lite.Document" /> with the given id.
        /// </summary>
        /// <returns>The <see cref="Couchbase.Lite.Document" />.</returns>
        /// <param name="id">The id of the Document to get or create.</param>
        public Document GetDocument(string id) 
        { 
            return GetDocument(id, false);
        }

        /// <summary>
        /// Gets the <see cref="Couchbase.Lite.Document" /> with the given id, or null if it does not exist.
        /// </summary>
        /// <returns>The <see cref="Couchbase.Lite.Document" /> with the given id, or null if it does not exist.</returns>
        /// <param name="id">The id of the Document to get.</param>
        public Document GetExistingDocument(String id) 
        { 
            return GetDocument(id, true);
        }

        /// <summary>
        /// Creates a <see cref="Couchbase.Lite.Document" /> with a unique id.
        /// </summary>
        /// <returns>A document with a unique id.</returns>
        public Document CreateDocument()
        { 
            return GetDocument(Misc.CreateGUID());
        }

        /// <summary>
        /// Gets the local document with the given id, or null if it does not exist.
        /// </summary>
        /// <returns>The existing local document.</returns>
        /// <param name="id">Identifier.</param>
        public IDictionary<String, Object> GetExistingLocalDocument(String id) 
        {
            if (!IsOpen) {
                Log.W(TAG, "GetExistingLocalDocument called on closed database");
                return null;
            }

            var gotRev = Storage.GetLocalDocument(MakeLocalDocumentId(id), null);
            return gotRev != null ? gotRev.GetProperties() : null;
        }

        /// <summary>
        /// Sets the contents of the local <see cref="Couchbase.Lite.Document" /> with the given id.  If <param name="properties"/> is null, the 
        /// <see cref="Couchbase.Lite.Document" /> is deleted.
        /// </summary>
        /// <param name="id">The id of the local document whos contents to set.</param>
        /// <param name="properties">The contents to set for the local document.</param>
        /// <exception cref="Couchbase.Lite.CouchbaseLiteException">Thrown if an issue occurs 
        /// while setting the contents of the local document.</exception>
        public bool PutLocalDocument(string id, IDictionary<string, object> properties) 
        { 
            if (!IsOpen) {
                Log.W(TAG, "PutLocalDocument called on closed database");
                return false;
            }

            id = MakeLocalDocumentId(id);
            var rev = new RevisionInternal(id, null, properties == null);
            if (properties != null) {
                rev.SetProperties(properties);
            }
                
            bool ok = Storage.PutLocalRevision(rev, null, false) != null;
            return ok;
        }

        /// <summary>
        /// Deletes the local <see cref="Couchbase.Lite.Document" /> with the given id.
        /// </summary>
        /// <returns><c>true</c>, if local <see cref="Couchbase.Lite.Document" /> was deleted, <c>false</c> otherwise.</returns>
        /// <param name="id">Identifier.</param>
        /// <exception cref="Couchbase.Lite.CouchbaseLiteException">Thrown if there is an issue occurs while deleting the local document.</exception>
        public bool DeleteLocalDocument(string id) 
        {
            return PutLocalDocument(id, null);
        } 

        /// <summary>
        /// Creates a <see cref="Couchbase.Lite.Query" /> that matches all <see cref="Couchbase.Lite.Document" />s in the <see cref="Couchbase.Lite.Database" />.
        /// </summary>
        /// <returns>Returns a <see cref="Couchbase.Lite.Query" /> that matches all <see cref="Couchbase.Lite.Document" />s in the <see cref="Couchbase.Lite.Database" />s.</returns>
        public Query CreateAllDocumentsQuery() 
        {
            if (!IsOpen) {
                Log.W(TAG, "CreateAllDocumentsQuery called on closed database");
                return null;
            }

            return new Query(this, (View)null);
        }

        /// <summary>
        /// Gets or creates the <see cref="Couchbase.Lite.View" /> with the given name.  
        /// New <see cref="Couchbase.Lite.View" />s won't be added to the <see cref="Couchbase.Lite.Database" /> 
        /// until a map function is assigned.
        /// </summary>
        /// <returns>The <see cref="Couchbase.Lite.View" /> with the given name.</returns>
        /// <param name="name">The name of the <see cref="Couchbase.Lite.View" /> to get or create.</param>
        public View GetView(String name) 
        {
            if (!IsOpen) {
                Log.W(TAG, "GetView called on closed database");
                return null;
            }

            View view = null;
            if (_views != null) {
                view = _views.Get(name);
            }

            if (view != null) {
                return view;
            }

            return RegisterView(View.MakeView(this, name, true));
        }

        /// <summary>
        /// Gets the <see cref="Couchbase.Lite.View" /> with the given name, or null if it does not exist.
        /// </summary>
        /// <returns>The <see cref="Couchbase.Lite.View" /> with the given name, or null if it does not exist.</returns>
        /// <param name="name">The name of the View to get.</param>
        public View GetExistingView(String name) 
        {
            if (!IsOpen) {
                Log.W(TAG, "GetExistingView called on closed database");
                return null;
            }

            View view = null;
            if (_views != null) {
                _views.TryGetValue(name, out view);
            }

            if (view != null) {
                return view;
            }

            view = View.MakeView(this, name, false);

            return RegisterView(view);
        }

        /// <summary>
        /// Gets the <see cref="ValidateDelegate" /> for the given name, or null if it does not exist.
        /// </summary>
        /// <returns>the <see cref="ValidateDelegate" /> for the given name, or null if it does not exist.</returns>
        /// <param name="name">The name of the validation delegate to get.</param>
        public ValidateDelegate GetValidation(String name) 
        {
            if (!IsOpen) {
                Log.W(TAG, "GetValidation called on closed database");
                return null;
            }

            ValidateDelegate retVal = null;
            if (!Shared.TryGetValue<ValidateDelegate>("validation", name, Name, out retVal)) {
                return null;
            }

            return retVal;
        }

        /// <summary>
        /// Sets the validation delegate for the given name. If delegate is null, 
        /// the validation with the given name is deleted. Before any change 
        /// to the <see cref="Couchbase.Lite.Database"/> is committed, including incoming changes from a pull 
        /// <see cref="Couchbase.Lite.Replication"/>, all of its validation delegates are called and given 
        /// a chance to reject it.
        /// </summary>
        /// <param name="name">The name of the validation delegate to set.</param>
        /// <param name="validationDelegate">The validation delegate to set.</param>
        public void SetValidation(string name, ValidateDelegate validationDelegate)
        {
            if (!IsOpen) {
                Log.W(TAG, "SetValidation called on closed database");
                return;
            }

            Shared.SetValue("validation", name, Name, validationDelegate);
        }

        /// <summary>
        /// Returns the <see cref="ValidateDelegate" /> for the given name, or null if it does not exist.
        /// </summary>
        /// <returns>The <see cref="ValidateDelegate" /> for the given name, or null if it does not exist.</returns>
        /// <param name="name">The name of the validation delegate to get.</param>
        /// <param name="status">The result of the operation</param>
        public FilterDelegate GetFilter(String name, Status status = null) 
        { 
            if (!IsOpen || name == null) {
                if (!IsOpen) {
                    Log.W(TAG, "GetFilter called on closed database");
                }

                return null;
            }

            FilterDelegate result = null;
            if (!Shared.TryGetValue("filter", name, Name, out result)) {
                result = null;
            }

            if (result == null) {
                var filterCompiler = FilterCompiler;
                if (filterCompiler == null) {
                    return null;
                }

                string language = null;
                var sourceCode = GetDesignDocFunction(name, "filters", out language) as string;

                if (sourceCode == null) {
                    if (status != null) {
                        status.Code = StatusCode.NotFound;
                    }
                    return null;
                }

                var filter = filterCompiler.CompileFilter(sourceCode, language);
                if (filter == null) {
                    if (status != null) {
                        status.Code = StatusCode.CallbackError;
                    }
                    Log.W(TAG, string.Format("Filter {0} failed to compile", name));
                    return null;
                }

                SetFilter(name, filter);
                return filter;
            }

            return result;
        }

        /// <summary>
        /// Sets the <see cref="ValidateDelegate" /> for the given name. If delegate is null, the filter 
        /// with the given name is deleted. Before a <see cref="Couchbase.Lite.Revision" /> is replicated via a 
        /// push <see cref="Couchbase.Lite.Replication" />, its filter delegate is called and 
        /// given a chance to exclude it from the <see cref="Couchbase.Lite.Replication" />.
        /// </summary>
        /// <param name="name">The name of the filter delegate to set.</param>
        /// <param name="filterDelegate">The filter delegate to set.</param>
        public void SetFilter(String name, FilterDelegate filterDelegate) 
        { 
            if (!IsOpen) {
                Log.W(TAG, "SetFilter called on closed database");
                return;
            }

            Shared.SetValue("filter", name, Name, filterDelegate);
        }

        /// <summary>
        /// Runs the <see cref="Couchbase.Lite.RunAsyncDelegate"/> asynchronously.
        /// </summary>
        /// <returns>The async task.</returns>
        /// <param name="runAsyncDelegate">The delegate to run asynchronously.</param>
        public Task RunAsync(RunAsyncDelegate runAsyncDelegate) 
        {
            return Manager.RunAsync(runAsyncDelegate, this);
        }

        /// <summary>
        /// Runs the delegate within a transaction. If the delegate returns false, 
        /// the transaction is rolled back.
        /// </summary>
        /// <returns>True if the transaction was committed, otherwise false.</returns>
        /// <param name="transactionDelegate">The delegate to run within a transaction.</param>
        public bool RunInTransaction(RunInTransactionDelegate transactionDelegate)
        {
            if (!IsOpen) {
                Log.W(TAG, "RunInTransaction called on closed database");
                return false;
            }

            return Storage.RunInTransaction(transactionDelegate);
        }

            
        /// <summary>
        /// Creates a new <see cref="Couchbase.Lite.Replication"/> that will push to the target <see cref="Couchbase.Lite.Database"/> at the given url.
        /// </summary>
        /// <returns>A new <see cref="Couchbase.Lite.Replication"/> that will push to the target <see cref="Couchbase.Lite.Database"/> at the given url.</returns>
        /// <param name="url">The url of the target Database.</param>
        public Replication CreatePushReplication(Uri url)
        {
            if (!IsOpen) {
                Log.W(TAG, "CreatePushReplication called on closed database");
                return null;
            }

            var scheduler = new SingleTaskThreadpoolScheduler();
            return new Pusher(this, url, false, new TaskFactory(scheduler));
        }

        /// <summary>
        /// Creates a new <see cref="Couchbase.Lite.Replication"/> that will pull from the source <see cref="Couchbase.Lite.Database"/> at the given url.
        /// </summary>
        /// <returns>A new <see cref="Couchbase.Lite.Replication"/> that will pull from the source Database at the given url.</returns>
        /// <param name="url">The url of the source Database.</param>
        public Replication CreatePullReplication(Uri url)
        {
            if (!IsOpen) {
                Log.W(TAG, "CreatePullReplication called on closed database");
                return null;
            }

            var scheduler = new SingleTaskThreadpoolScheduler();
            return new Puller(this, url, false, new TaskFactory(scheduler));
        }

        /// <summary>
        /// Creates a new <see cref="Couchbase.Lite.Replication"/> that will pull attachments from the source <see cref="Couchbase.Lite.Database"/> at the given url.
        /// </summary>
        /// <returns>A new <see cref="Couchbase.Lite.Replication"/> that will pull attachments from the source Database at the given url.</returns>
        /// <param name="url">The url of the source Database.</param>
        public Replication CreateAttachmentReplication(Uri url)
        {
            var scheduler = new SingleTaskThreadpoolScheduler();
            return new AttachmentPuller(this, url, false, new TaskFactory(scheduler));
        }

        /// <summary>
        /// Returns a <see cref="System.String"/> that represents the current <see cref="Couchbase.Lite.Database"/>.
        /// </summary>
        /// <returns>A <see cref="System.String"/> that represents the current <see cref="Couchbase.Lite.Database"/>.</returns>
        public override string ToString()
        {
            return "Database[" + DbDirectory + "]";
        }

        /// <summary>
        /// Event handler delegate that will be called whenever a <see cref="Couchbase.Lite.Document"/> within the <see cref="Couchbase.Lite.Database"/> changes.
        /// </summary>
        public event EventHandler<DatabaseChangeEventArgs> Changed {
            add { _changed = (EventHandler<DatabaseChangeEventArgs>)Delegate.Combine(_changed, value); }
            remove { _changed = (EventHandler<DatabaseChangeEventArgs>)Delegate.Remove(_changed, value); }
        }
        private EventHandler<DatabaseChangeEventArgs> _changed;

        /// <summary>
        /// Change the encryption key used to secure this database
        /// </summary>
        /// <param name="newKey">The new key to use</param>
        public void ChangeEncryptionKey(SymmetricKey newKey)
        {
            if (!IsOpen) {
                Log.W(TAG, "ChangeEncryptionKey called on closed database");
                return;
            }

            var action = Storage.ActionToChangeEncryptionKey(newKey);
            action.AddLogic(Attachments.ActionToChangeEncryptionKey(newKey));
            action.AddLogic(() => Shared.SetValue("encryptionKey", "", Name, newKey), null, null);
            action.Run();
        }

        #endregion

        #region Internal Methods

        /* Create a local checkpoint document. This method is called only when importing or
        replacing the database. The local checkpoint contains the old localUUID of the database 
        before importing. The old localUUID is used by replicators to get the local checkpoint 
        from the imported database in order to start replicating from from the current local       
        checkpoint of the imported database after importing. */
        internal void CreateLocalCheckpointDoc()
        {

            var document = new Dictionary<string, object> { { CHECKPOINT_LOCAL_UUID_KEY, PrivateUUID() } };
            try {
                PutLocalDocument(LOCAL_CHECKPOINT_DOC_ID, document);
            } catch(CouchbaseLiteException) {
                Log.E(TAG, "Could not create local checkpoint document");
                throw;
            } catch(Exception e) {
                throw new CouchbaseLiteException("Error creating local checkpoint document", e);
            }
        }

        internal object GetLocalCheckpointDocValue(string key)
        {
            if (key == null) {
                return null;
            }

            var document = GetExistingLocalDocument(LOCAL_CHECKPOINT_DOC_ID) ?? new Dictionary<string, object>();
            return document.Get(key);
        }

        internal void PutLocalCheckpointDoc(string key, object value) {
            if (key == null || value == null) {
                return;
            }

            var document = GetExistingLocalDocument(LOCAL_CHECKPOINT_DOC_ID) ?? new Dictionary<string, object>();
            document[key] = value;
            PutLocalDocument(LOCAL_CHECKPOINT_DOC_ID, document);
        }

        /* Returns local checkpoint document if it exists. Otherwise returns null. */
        internal IDictionary<string, object> GetLocalCheckpointDoc()
        {
            return GetExistingLocalDocument(LOCAL_CHECKPOINT_DOC_ID);
        }

        // This is ONLY FOR TESTS
        internal BlobStoreWriter AttachmentWriterForAttachment(IDictionary<string, object> attachment)
        {
            var digest = attachment.GetCast<string>("digest");
            if (digest == null) {
                return null;
            }

            return _pendingAttachmentsByDigest.Get(digest);
        }


        internal static IDictionary<string, object> StripDocumentJSON(IDictionary<string, object> originalProps)
        {
            // Don't leave in any "_"-prefixed keys except for the ones in SPECIAL_KEYS_TO_LEAVE.
            // Keys in SPECIAL_KEYS_TO_REMOVE (_id, _rev, ...) are left out, any others trigger an error.
            var properties = new Dictionary<string, object>(originalProps.Count);
            foreach (var pair in originalProps) {
                if (!pair.Key.StartsWith("_") || SPECIAL_KEYS_TO_LEAVE.Contains(pair.Key)) {
                    properties[pair.Key] = pair.Value;
                } else if (!SPECIAL_KEYS_TO_REMOVE.Contains(pair.Key)) {
                    Log.W(TAG, "Invalid top-level key '{0}' in document to be inserted", pair.Key);
                    return null;
                }
            }

            return properties;
        }

        internal RevisionList UnpushedRevisionsSince(string sequence, FilterDelegate filter, IDictionary<string, object> filterParams)
        {
            // Include conflicts so all conflicting revisions are replicated too
            var options = ChangesOptions.Default;
            options.IncludeConflicts = true;

            return ChangesSince(Int64.Parse(sequence ?? "0"), options, filter, filterParams);
        }

        internal object GetDesignDocFunction(string fnName, string key, out string language)
        {
            language = null;
            var path = fnName.Split('/');
            if (path.Length != 2) {
                return null;
            }

            var docId = string.Format("_design/{0}", path[0]);
            var rev = GetDocumentWithIDAndRev(docId, null, true);
            if (rev == null) {
                return null;
            }

            var outLanguage = (string)rev.GetPropertyForKey("language");
            if (outLanguage != null) {
                language = outLanguage;
            }
            else {
                language = "javascript";
            }

            var container = rev.GetPropertyForKey(key).AsDictionary<string, object>();
            if (container == null) {
                return null;
            }

            return container.Get(path[1]);
        }

        /// <exception cref="Couchbase.Lite.CouchbaseLiteException">When attempting to add an invalid revision</exception>
        internal void ForceInsert(RevisionInternal inRev, IList<string> revHistory, Uri source)
        {
            if (!IsOpen) {
                throw new CouchbaseLiteException("DB is closed", StatusCode.DbError);
            }

            if (revHistory == null) {
                revHistory = new List<string>(0);
            }

            var rev = inRev.CopyWithDocID(inRev.GetDocId(), inRev.GetRevId());
            rev.SetSequence(0);
            string revID = rev.GetRevId();
            if (!Document.IsValidDocumentId(rev.GetDocId()) || revID == null) {
                throw new CouchbaseLiteException(StatusCode.BadId);
            }

            if (revHistory.Count == 0) {
                revHistory.Add(revID);
            } else if (revID != revHistory[0]) {
                throw new CouchbaseLiteException(StatusCode.BadId);
            }

            // skip processing attachments at this time if they are defered
            if (ManagerOptions.Default.DownloadAttachmentsOnSync == true && inRev.GetAttachments() != null) {
                var updatedRev = inRev.CopyWithDocID(inRev.GetDocId(), inRev.GetRevId());
                ProcessAttachmentsForRevision(updatedRev, revHistory.Skip(1).Take(revHistory.Count-1).ToList());
                inRev = updatedRev;
            }

            StoreValidation validationBlock = null;
            if (Shared != null && Shared.HasValues("validation", Name)) {
                validationBlock = ValidateRevision;
            }

            Storage.ForceInsert(inRev, revHistory, validationBlock, source);
        }

        internal bool AddReplication(Replication replication)
		{
			if(replication.IsAttachmentPull)
			{
				return true;
			}

            lock (_allReplicatorsLocker) {
                if (AllReplications.All(x => x.RemoteCheckpointDocID() != replication.RemoteCheckpointDocID())) {
                    AllReplicators.Add(replication);
                    return true;
                }

                return false;

            }
        }

        internal void ForgetReplication(Replication replication)
        {
            lock (_allReplicatorsLocker) { 
                AllReplicators.Remove(replication); 
            }
        }

        internal bool AddActiveReplication(Replication replication)
        {
            if (ActiveReplicators == null) {
                Log.W(TAG, "ActiveReplicators is null, so replication will not be added");
                return false;
            }

            if (ActiveReplicators.All(x => x.RemoteCheckpointDocID() != replication.RemoteCheckpointDocID())) {
                ActiveReplicators.Add(replication);
            } else {
                return false;
            }

            replication.Changed += (sender, e) => {
                if (e.Source != null && !e.Source.IsRunning && ActiveReplicators != null) {
                    ActiveReplicators.Remove(e.Source);
                }
            };

            return true;
        }

        internal bool Exists()
        {
            return Directory.Exists(DbDirectory);
        }

        internal static string MakeLocalDocumentId(string documentId)
        {
            return string.Format("_local/{0}", documentId);
        }

        internal void SetLastSequence(string lastSequence, string checkpointId)
        {
            if (lastSequence == "0") {
                Log.I(TAG, "SetLastSequence called with 0 for {0}, ignoring", checkpointId);
                return;
            }

            if (!IsOpen || Storage == null || !Storage.IsOpen) {
                Log.I(TAG, "Storage is null or closed, so not attempting to set last sequence");
                return;
            }

            Storage.SetInfo(CheckpointInfoKey(checkpointId), lastSequence);
        }

        internal string LastSequenceWithCheckpointId(string checkpointId)
        {
            if (Storage == null || !Storage.IsOpen) {
                return String.Empty;
            }

            return Storage.GetInfo(CheckpointInfoKey(checkpointId));
        }
 
        internal IEnumerable<QueryRow> QueryViewNamed(String viewName, QueryOptions options, long ifChangedSince, ValueTypePtr<long> outLastSequence)
        {
            IEnumerable<QueryRow> iterator = null;
            long lastIndexedSequence = 0, lastChangedSequence = 0;
            if(viewName != null) {
                var view = GetView(viewName);
                if(view == null) {
                    throw new CouchbaseLiteException(StatusCode.NotFound);
                }

                lastIndexedSequence = view.LastSequenceIndexed;
                if(options.Stale == IndexUpdateMode.Before || lastIndexedSequence <= 0) {
                    var status = view.UpdateIndex();
                    if(status.IsError) {
                        Log.W(TAG, "Failed to update index: {0}", status.Code);
                        throw new CouchbaseLiteException(status.Code);
                    }

                    lastIndexedSequence = view.LastSequenceIndexed;
                } else if(options.Stale == IndexUpdateMode.After && lastIndexedSequence <= GetLastSequenceNumber()) {
                    RunAsync(d => view.UpdateIndex());
                }

                lastChangedSequence = view.LastSequenceChangedAt;
                iterator = view.QueryWithOptions(options);
            } else { // null view means query _all_docs
                iterator = GetAllDocs(options);
                lastIndexedSequence = lastChangedSequence = GetLastSequenceNumber();
            }

            outLastSequence.Value = lastIndexedSequence;

            return iterator;
        }
            
        internal RevisionList ChangesSince(long lastSeq, ChangesOptions options, FilterDelegate filter, IDictionary<string, object> filterParams)
        {
            RevisionFilter revFilter = null;
            if (filter != null) {
                revFilter = (rev => RunFilter(filter, filterParams, rev));
            }

            return Storage.ChangesSince(lastSeq, options, revFilter);
        }

        internal bool RunFilter(FilterDelegate filter, IDictionary<string, object> filterParams, RevisionInternal rev)
        {
            if (filter == null) {
                return true;
            }

            var publicRev = new SavedRevision(this, rev);
            return filter(publicRev, filterParams);
        }

        internal IEnumerable<QueryRow> GetAllDocs(QueryOptions options)
        {
            // For regular all-docs, let storage do it all:
            if (options == null || options.AllDocsMode != AllDocsMode.BySequence) {
                return Storage.GetAllDocs(options);
            }

            if (options.Descending) {
                throw new CouchbaseLiteException("Descending all docs not implemented", StatusCode.NotImplemented);
            }

            ChangesOptions changesOpts = new ChangesOptions();
            changesOpts.Limit = options.Limit;
            changesOpts.IncludeDocs = options.IncludeDocs;
            changesOpts.IncludeConflicts = true;
            changesOpts.SortBySequence = true;

            long startSeq = KeyToSequence(options.StartKey, 1);
            long endSeq = KeyToSequence(options.EndKey, long.MaxValue);
            if (!options.InclusiveStart) {
                ++startSeq;
            }

            if (!options.InclusiveEnd) {
                --endSeq;
            }

            long minSeq = startSeq, maxSeq = endSeq;
            if (minSeq > maxSeq) {
                return null; // empty result
            }

            RevisionList revs = Storage.ChangesSince(minSeq - 1, changesOpts, null);
            if (revs == null) {
                return null;
            }

            var result = new List<QueryRow>();
            var revEnum = options.Descending ? revs.Reverse<RevisionInternal>() : revs;
            foreach (var rev in revEnum) {
                long seq = rev.GetSequence();
                if (seq < minSeq || seq > maxSeq) {
                    break;
                }

                var value = new NonNullDictionary<string, object> {
                    { "rev", rev.GetRevId() },
                    { "deleted", rev.IsDeleted() ? (object)true : null }
                };
                result.Add(new QueryRow(rev.GetDocId(), seq, rev.GetDocId(), value, rev, null));
            }

            return result;
        }

        internal static string JoinQuotedObjects(IEnumerable<Object> objects)
        {
            var strings = new List<String>();
            foreach (var obj in objects)
            {
                strings.Add(obj != null ? obj.ToString() : null);
            }
            return JoinQuoted(strings);
        }

        internal static string JoinQuoted(IEnumerable<string> strings)
        {
            if (!strings.Any()) {
                return String.Empty;
            }

            var result = "'";
            var first = true;

            foreach (string str in strings)
            {
                if (first)
                    first = false;
                else
                    result = result + "','";

                result = result + Quote(str);
            }

            result = result + "'";

            return result;
        }

        internal static string Quote(string str)
        {
            return str.Replace("'", "''");
        }

        internal View RegisterView(View view)
        {
            if (view == null) {
                return null;
            }

            if (_views == null) {
                _views = new Dictionary<string, View>();
            }

            _views.Put(view.Name, view);
            return view;
        }

        internal View MakeAnonymousView()
        {
            for (var i = 0; true; ++i)
            {
                var name = String.Format("anon{0}", i);
                var existing = GetExistingView(name);
                if (existing == null)
                {
                    // this name has not been used yet, so let's use it
                    return GetView(name);
                }
            }
        }

        internal IList<View> GetAllViews()
        {
            var enumerator = from viewName in Storage.GetAllViews()
                                      select GetExistingView(viewName);

            return enumerator.ToList();
        }

        internal void ForgetView(string viewName) {
            _views.Remove(viewName);
        }

        /// <summary>
        /// Creates a one-shot query with the given map function. This is equivalent to creating an
        /// anonymous View and then deleting it immediately after querying it. It may be useful during
        /// development, but in general this is inefficient if this map will be used more than once,
        /// because the entire view has to be regenerated from scratch every time.
        /// </summary>
        /// <returns>The query.</returns>
        /// <param name="map">Map.</param>
        internal Query SlowQuery(MapDelegate map) 
        {
            return new Query(this, map);
        }

        internal void RemoveDocumentFromCache(Document document)
        {
            DocumentCache.Remove(document.Id);
            var dummy = default(WeakReference);
            UnsavedRevisionDocumentCache.TryRemove(document.Id, out dummy);
        }

        internal string PrivateUUID ()
        {
            return Storage.GetInfo("privateUUID");
        }

        internal string PublicUUID()
        {
            return Storage.GetInfo("publicUUID");
        }

        internal void ReplaceUUIDs(string privUUID = null, string pubUUID = null)
        {
            if (privUUID == null) {
                privUUID = Misc.CreateGUID();
            }

            if (pubUUID == null) {
                pubUUID = Misc.CreateGUID();
            }

            Storage.SetInfo("publicUUID", pubUUID);
            Storage.SetInfo("privateUUID", privUUID);
        }

        internal void RememberAttachmentWritersForDigests(IDictionary<String, BlobStoreWriter> blobsByDigest)
        {
            PendingAttachmentsByDigest.PutAll(blobsByDigest);
        }

        internal void RememberAttachmentWriter (BlobStoreWriter writer)
        {
            var digest = writer.SHA1DigestString();
            PendingAttachmentsByDigest[digest] = writer;
        }

        internal RevisionInternal GetDocument(string docId, string revId, bool withBody, Status outStatus = null)
        {
            return Storage.GetDocument(docId, revId, withBody, outStatus);
        }

        internal static IDictionary<string, object> MakeRevisionHistoryDict(IList<RevisionInternal> history)
        {
            if (history == null)
                return null;

            // Try to extract descending numeric prefixes:
            var suffixes = new List<string>();
            var start = -1;
            var lastRevNo = -1;

            foreach (var rev in history) {
                var parsed = RevisionInternal.ParseRevId(rev.GetRevId());
                int revNo = parsed.Item1;
                string suffix = parsed.Item2;
                if (revNo > 0 && suffix.Length > 0) {
                    if (start < 0) {
                        start = revNo;
                    }
                    else {
                        if (revNo != lastRevNo - 1) {
                            start = -1;
                            break;
                        }
                    }
                    lastRevNo = revNo;
                    suffixes.Add(suffix);
                }
                else {
                    start = -1;
                    break;
                }
            }

            var result = new Dictionary<String, Object>();
            if (start == -1) {
                // we failed to build sequence, just stuff all the revs in list
                suffixes = new List<string>();
                foreach (RevisionInternal rev_1 in history) {
                    suffixes.Add(rev_1.GetRevId());
                }
            }
            else {
                result["start"] = start;
            }

            result["ids"] = suffixes;
            return result;
        }

        /// <summary>Parses the _revisions dict from a document into an array of revision ID strings.</summary>
        internal static IList<string> ParseCouchDBRevisionHistory(IDictionary<String, Object> docProperties)
        {
            var revisions = docProperties.Get ("_revisions").AsDictionary<string,object> ();
            if (revisions == null) {
                return new List<string>();
            }

            var ids = revisions ["ids"].AsList<string> ();
            if (ids == null || ids.Count == 0) {
                return new List<string>();
            }

            var revIDs = new List<string>(ids);
            var start = Convert.ToInt64(revisions.Get("start"));
            for (var i = 0; i < revIDs.Count; i++) {
                var revID = revIDs[i];
                revIDs.Set(i, start-- + "-" + revID);
            }

            return revIDs;
        }

        /// <exception cref="Couchbase.Lite.CouchbaseLiteException"></exception>
        internal RevisionInternal PutRevision(RevisionInternal rev, String prevRevId)
        {
            return PutRevision(rev, prevRevId, false);
        }

        internal RevisionInternal PutDocument(string docId, IDictionary<string, object> properties, string prevRevId, bool allowConflict)
        {
            bool deleting = properties == null || properties.GetCast<bool>("_deleted");
            Log.D(TAG, "PUT _id={0}, _rev={1}, _deleted={2}, allowConflict={3}", docId, prevRevId, deleting, allowConflict);
            if ((prevRevId != null && docId == null) || (deleting && docId == null)) {
                throw new CouchbaseLiteException(StatusCode.BadId);
            }

            if (properties != null && properties.Get("_attachments").AsDictionary<string, object>() != null) {
                var tmpRev = new RevisionInternal(docId, prevRevId, deleting);
                tmpRev.SetProperties(properties);
                if (!ProcessAttachmentsForRevision(tmpRev, prevRevId == null ? null : new List<string> { prevRevId })) {
                    return null;
                }

                properties = tmpRev.GetProperties();
            }

            StoreValidation validationBlock = null;
            if (Shared.HasValues("validation", Name)) {
                validationBlock = ValidateRevision;
            }

            var putRev = Storage.PutRevision(docId, prevRevId, properties, deleting, allowConflict, validationBlock);
            if (putRev != null) {
                Log.D(TAG, "--> created {0}", putRev);
                if (!string.IsNullOrEmpty(docId)) {
                    var dummy = default(WeakReference);
                    UnsavedRevisionDocumentCache.TryRemove(docId, out dummy);
                }
            }

            return putRev;
        }

        /// <summary>Stores a new (or initial) revision of a document.</summary>
        /// <remarks>
        /// Stores a new (or initial) revision of a document.
        /// This is what's invoked by a PUT or POST. As with those, the previous revision ID must be supplied when necessary and the call will fail if it doesn't match.
        /// </remarks>
        /// <param name="oldRev">The revision to add. If the docID is null, a new UUID will be assigned. Its revID must be null. It must have a JSON body.
        ///     </param>
        /// <param name="prevRevId">The ID of the revision to replace (same as the "?rev=" parameter to a PUT), or null if this is a new document.
        ///     </param>
        /// <param name="allowConflict">If false, an error status 409 will be returned if the insertion would create a conflict, i.e. if the previous revision already has a child.
        ///     </param>
        /// <returns>A new RevisionInternal with the docID, revID and sequence filled in (but no body).
        ///     </returns>
        /// <exception cref="Couchbase.Lite.CouchbaseLiteException"></exception>
        internal RevisionInternal PutRevision(RevisionInternal oldRev, string prevRevId, bool allowConflict)
        {
            return PutDocument(oldRev.GetDocId(), oldRev.GetProperties(), prevRevId, allowConflict);
        }

        internal bool PostChangeNotifications()
        {
            bool posted = false;

            // This is a 'while' instead of an 'if' because when we finish posting notifications, there
            // might be new ones that have arrived as a result of notification handlers making document
            // changes of their own (the replicator manager will do this.) So we need to check again.
            while ((Storage == null || !Storage.InTransaction) && IsOpen && !_isPostingChangeNotifications 
                && _changesToNotify != null && _changesToNotify.Count > 0) {
                try {
                    _isPostingChangeNotifications = true;

                    IList<DocumentChange> outgoingChanges = new List<DocumentChange>();
                    foreach (var change in _changesToNotify) {
                        outgoingChanges.Add(change);
                    }
                    _changesToNotify.Clear();
                    // TODO: change this to match iOS and call cachedDocumentWithID
                    var isExternal = false;
                    foreach (var change in outgoingChanges) {
                        var document = GetDocument(change.DocumentId);
                        if(document == null) {
                            continue;
                        }

                        document.RevisionAdded(change, true);
                        if (change.SourceUrl != null) {
                            isExternal = true;
                        }
                    }

                    var args = new DatabaseChangeEventArgs { 
                        Changes = outgoingChanges,
                        IsExternal = isExternal,
                        Source = this
                    };

                    var changeEvent = _changed;
                    if (changeEvent != null)
                        changeEvent(this, args);

                    posted = true;
                } catch (Exception e) {
                    Log.E(TAG, "Got exception posting change notifications", e);
                } finally {
                    _isPostingChangeNotifications = false;
                }
            }
                
            return posted;
        }

        internal void NotifyChange(RevisionInternal rev, RevisionInternal winningRev, Uri source, bool inConflict)
        {
            var change = new DocumentChange(rev, winningRev.GetRevId(), inConflict, source);
            _changesToNotify.Add(change);

            if (!PostChangeNotifications())
            {
                // The notification wasn't posted yet, probably because a transaction is open.
                // But the Document, if any, needs to know right away so it can update its
                // currentRevision.
                var doc = DocumentCache.Get(change.DocumentId);
                if (doc != null) {
                    doc.RevisionAdded(change, false);
                }
            }
        }

        /// <summary>
        /// Given a newly-added revision, adds the necessary attachment rows to the sqliteDb and
        /// stores inline attachments into the blob store.
        /// </summary>
        /// <remarks>
        /// Given a newly-added revision, adds the necessary attachment rows to the sqliteDb and
        /// stores inline attachments into the blob store.
        /// </remarks>
        /// <exception cref="Couchbase.Lite.CouchbaseLiteException"></exception>
        internal void ProcessAttachmentsForRevision(IDictionary<string, AttachmentInternal> attachments, RevisionInternal rev, long parentSequence)
        {
            Debug.Assert((rev != null));
            var newSequence = rev.GetSequence();
            Debug.Assert((newSequence > parentSequence));
            var generation = rev.GetGeneration();
            Debug.Assert((generation > 0));

            // If there are no attachments in the new rev, there's nothing to do:
            IDictionary<string, object> revAttachments = null;
            var properties = rev.GetProperties ();
            if (properties != null) {
                revAttachments = properties.Get("_attachments").AsDictionary<string, object>();
            }

            if (revAttachments == null || revAttachments.Count == 0 || rev.IsDeleted()) {
                return;
            }

            foreach (string name in revAttachments.Keys)
            {
                var attachment = attachments.Get(name);
                if (attachment != null)
                {
                    // Determine the revpos, i.e. generation # this was added in. Usually this is
                    // implicit, but a rev being pulled in replication will have it set already.
                    if (attachment.RevPos == 0)
                    {
                        attachment.RevPos = generation;
                    }
                    else
                    {
                        if (attachment.RevPos > generation)
                        {
                            Log.W(TAG, string.Format("Attachment {0} {1} has unexpected revpos {2}, setting to {3}", rev, name, attachment.RevPos, generation));
                            attachment.RevPos = generation;
                        }
                    }
                }
            }
        }

        /// <exception cref="Couchbase.Lite.CouchbaseLiteException"></exception>
        internal void InstallAttachment(AttachmentInternal attachment)
        {
            var digest = attachment.Digest;
            if (digest == null) {
                throw new CouchbaseLiteException(StatusCode.BadAttachment);
            }

            if (PendingAttachmentsByDigest != null && PendingAttachmentsByDigest.ContainsKey(digest)) {
                var writer = PendingAttachmentsByDigest.Get(digest);
                try {
                    var blobStoreWriter = writer;
                    blobStoreWriter.Install();
                    attachment.BlobKey = (blobStoreWriter.GetBlobKey());
                    attachment.Length = blobStoreWriter.GetLength();
                }
                catch (Exception e) {
                    throw new CouchbaseLiteException(e, StatusCode.AttachmentError);
                }
            }
        }

        internal Uri FileForAttachmentDict(IDictionary<String, Object> attachmentDict)
        {
            if (!IsOpen) {
                Log.W(TAG, "FileForAttachmentDict called on closed database");
                return null;
            }

            var digest = (string)attachmentDict.Get("digest");
            if (digest == null) {
                return null;
            }

            string path = null;
            var pending = PendingAttachmentsByDigest.Get(digest);
            if (pending != null) {
                path = pending.FilePath;
            } else {
                // If it's an installed attachment, ask the blob-store for it:
                var key = new BlobKey(digest);
                path = Attachments.PathForKey(key);
            }

            Uri retVal = null;
            if (!Uri.TryCreate(path, UriKind.RelativeOrAbsolute, out retVal)) {
                return null;
            }

            return retVal;
        }

        internal IList<RevisionInternal> GetRevisionHistory(RevisionInternal rev, IList<string> ancestorRevIds)
        {
            if (!IsOpen) {
                Log.W(TAG, "GetRevisionHistory called on closed database");
                return null;
            }

            HashSet<string> ancestors = ancestorRevIds != null ? new HashSet<string>(ancestorRevIds) : null;
            return Storage.GetRevisionHistory(rev, ancestors);
        }

        internal void ExpandAttachments(RevisionInternal rev, int minRevPos, bool allowFollows, 
            bool decodeAttachments)
        {
            if (!IsOpen) {
                throw new CouchbaseLiteException("DB is closed", StatusCode.DbError);
            }

            rev.MutateAttachments((name, attachment) =>
            {
                var revPos = attachment.GetCast<long>("revpos");
                if(revPos < minRevPos && revPos != 0) {
                    //Stub:
                    return new Dictionary<string, object> { { "stub", true }, { "revpos", revPos } };
                }

                var expanded = new Dictionary<string, object>(attachment);
                expanded.Remove("stub");
                if(decodeAttachments) {
                    expanded.Remove("encoding");
                    expanded.Remove("encoded_length");
                }

                if(allowFollows && SmallestLength(expanded) >= Database.BIG_ATTACHMENT_LENGTH) {
                    //Data will follow (multipart):
                    expanded["follows"] = true;
                    expanded.Remove("data");
                } else {
                    //Put data inline:
                    expanded.Remove("follows");
                    var attachObj = AttachmentForDict(attachment, name);
                    var data = decodeAttachments ? attachObj.Content : attachObj.EncodedContent;
                    if(data == null) {
                        Log.W(TAG, "Can't get binary data of attachment '{0}' of {1}", name, rev);
                        return attachment;
                    }

                    expanded["data"] = Convert.ToBase64String(data.ToArray());
                }
                    
                return expanded;
            });
        }

        internal AttachmentInternal AttachmentForDict(IDictionary<string, object> info, string filename)
        {
            if (info == null) {
                return null;
            }

            AttachmentInternal attachment = new AttachmentInternal(filename, info);
            attachment.Database = this;
            return attachment;
        }
            
        internal static void StubOutAttachmentsInRevBeforeRevPos(RevisionInternal rev, long minRevPos, bool attachmentsFollow)
        {
            if (minRevPos <= 1 && !attachmentsFollow)
            {
                return;
            }

            rev.MutateAttachments((name, attachment) =>
            {
                var revPos = 0L;
                if (attachment.ContainsKey("revpos"))
                {
                    revPos = Convert.ToInt64(attachment["revpos"]);
                }

                var includeAttachment = (revPos == 0 || revPos >= minRevPos);
                var stubItOut = !includeAttachment && (!attachment.ContainsKey("stub") || (bool)attachment["stub"] == false);
                var addFollows = includeAttachment && attachmentsFollow && (!attachment.ContainsKey("follows") || (bool)attachment["follows"] == false);

                if (!stubItOut && !addFollows)
                {
                    return attachment; // no change
                }

                // Need to modify attachment entry
                var editedAttachment = new Dictionary<string, object>(attachment);
                editedAttachment.Remove("data");

                if (stubItOut)
                {
                    // ...then remove the 'data' and 'follows' key:
                    editedAttachment.Remove("follows");
                    editedAttachment["stub"] = true;
                    Log.V(TAG, String.Format("Stubbed out attachment {0}: revpos {1} < {2}", rev, revPos, minRevPos));
                }
                else if (addFollows)
                {
                    editedAttachment.Remove("stub");
                    editedAttachment["follows"] = true;
                    Log.V(TAG, String.Format("Added 'follows' for attachment {0}: revpos {1} >= {2}", rev, revPos, minRevPos));
                }

                return editedAttachment;
            });
        }
            
        internal bool ProcessAttachmentsForRevision(RevisionInternal rev, IList<string> ancestry)
        {
            var revAttachments = rev.GetAttachments();
            if (revAttachments == null) {
                return true; // no-op: no attachments
            }

            // Deletions can't have attachments:
            if (rev.IsDeleted() || revAttachments.Count == 0) {
                var body = rev.GetProperties();
                body.Remove("_attachments");
                rev.SetProperties(body);
                return true;
            }

            var prevRevId = ancestry != null && ancestry.Count > 0 ? ancestry[0] : null;
            int generation = RevisionInternal.GenerationFromRevID(prevRevId) + 1;
            IDictionary<string, object> parentAttachments = null;
            return rev.MutateAttachments((name, attachInfo) =>
            {
                AttachmentInternal attachment = null;
                try {
                    attachment = new AttachmentInternal(name, attachInfo);
                } catch(CouchbaseLiteException) {
                    return null;
                }

                if(attachment.EncodedContent != null) {
                    // If there's inline attachment data, decode and store it:
                    BlobKey blobKey = new BlobKey();
                    if(!Attachments.StoreBlob(attachment.EncodedContent.ToArray(), blobKey)) {
                        throw new CouchbaseLiteException(
                            String.Format("Failed to write attachment ' {0}'to disk", name), StatusCode.AttachmentError);
                    }

                    attachment.BlobKey = blobKey;
                } else if(attachInfo.GetCast<bool>("follows")) {
                    // "follows" means the uploader provided the attachment in a separate MIME part.
                    // This means it's already been registered in _pendingAttachmentsByDigest;
                    // I just need to look it up by its "digest" property and install it into the store:
                    InstallAttachment(attachment);
                } else if(attachInfo.GetCast<bool>("stub")) {
                    // "stub" on an incoming revision means the attachment is the same as in the parent.
                    if(parentAttachments == null && prevRevId != null) {
                        parentAttachments = GetAttachmentsFromDoc(rev.GetDocId(), prevRevId);
                        if(parentAttachments == null) {
                            if(Attachments.HasBlobForKey(attachment.BlobKey)) {
                                // Parent revision's body isn't known (we are probably pulling a rev along
                                // with its entire history) but it's OK, we have the attachment already
                                return attachInfo;
                            }

                            var ancestorAttachment = FindAttachment(name, attachment.RevPos, rev.GetDocId(), ancestry);
                            if(ancestorAttachment != null) {
                                return ancestorAttachment;
                            }

                            throw new CouchbaseLiteException(
                                String.Format("Unable to find 'stub' attachment {0} in history", name), StatusCode.BadAttachment);
                        }
                    }

                    var parentAttachment = parentAttachments == null ? null : parentAttachments.Get(name).AsDictionary<string, object>();
                    if(parentAttachment == null) {
                        throw new CouchbaseLiteException(
                            String.Format("Unable to find 'stub' attachment {0} in history", name), StatusCode.BadAttachment);
                    }

                    return parentAttachment;
                }


                // Set or validate the revpos:
                if(attachment.RevPos == 0) {
                    attachment.RevPos = generation;
                } else if(attachment.RevPos > generation) {
                    throw new CouchbaseLiteException(
                        String.Format("Attachment specifies revision generation {0} but document is only at revision generation {1}",
                        attachment.RevPos, generation), StatusCode.BadAttachment);
                }

                Debug.Assert(attachment.IsValid);
                return attachment.AsStubDictionary();
            });
        }

        internal IDictionary<string, object> FindAttachment(string name, int revPos, string docId, IList<string> ancestry)
        {
            if (ancestry == null) {
                return null;
            }

            for (var i = ancestry.Count - 1; i >= 0; i--) {
                var revID = ancestry[i];
                if (RevisionInternal.GenerationFromRevID(revID) >= revPos) {
                    var attachments = GetAttachmentsFromDoc(docId, revID);
                    if (attachments == null) {
                        continue;
                    }

                    var attachment = attachments.Get(name).AsDictionary<string, object>();
                    if (attachment != null) {
                        return attachment;
                    }
                }
            }

            return null;
        }

        internal IDictionary<string, object> GetAttachmentsFromDoc(string docId, string revId)
        {
            var rev = new RevisionInternal(docId, revId, false);
            LoadRevisionBody(rev);
            return rev.GetAttachments();
        }
            
        internal AttachmentInternal GetAttachmentForRevision(RevisionInternal rev, string name)
        {
            Debug.Assert(name != null);
            var attachments = rev.GetAttachments();
            if (attachments == null) {
                rev = LoadRevisionBody(rev);
                attachments = rev.GetAttachments();
                if (attachments == null) {
                    return null;
                }
            }

            return AttachmentForDict(attachments.Get(name).AsDictionary<string, object>(), name);
        }
            
        internal RevisionInternal RevisionByLoadingBody(RevisionInternal rev, Status outStatus)
        {
            // First check for no-op -- if we just need the default properties and already have them:
            if (rev.GetSequence() != 0) {
                var props = rev.GetProperties();
                if (props != null && props.ContainsKey("_rev") && props.ContainsKey("_id")) {
                    if (outStatus != null) {
                        outStatus.Code = StatusCode.Ok;
                    }

                    return rev;
                }
            }

            RevisionInternal nuRev = rev.CopyWithDocID(rev.GetDocId(), rev.GetRevId());
            try {
                LoadRevisionBody(nuRev);
            } catch(CouchbaseLiteException e) {
                if (outStatus != null) {
                    outStatus.Code = e.CBLStatus.Code;
                }

                nuRev = null;
            }

            return nuRev;
        }
            
        //Doesn't handle CouchbaseLiteException
        internal RevisionInternal LoadRevisionBody(RevisionInternal rev)
        {
            if (!IsOpen) {
                Log.W(TAG, "LoadRevisionBody called on closed database");
                return null;
            }

            if (rev.GetSequence() > 0) {
                var props = rev.GetProperties();
                if (props != null && props.GetCast<string>("_rev") != null && props.GetCast<string>("_id") != null) {
                    return rev;
                }
            }



            Debug.Assert(rev.GetDocId() != null && rev.GetRevId() != null);
            Storage.LoadRevisionBody(rev);
            return rev;
        }

        /// <summary>Updates or deletes an attachment, creating a new document revision in the process.
        ///     </summary>
        /// <remarks>
        /// Updates or deletes an attachment, creating a new document revision in the process.
        /// Used by the PUT / DELETE methods called on attachment URLs.
        /// </remarks>
        /// <exclude></exclude>
        /// <exception cref="Couchbase.Lite.CouchbaseLiteException"></exception>
        internal RevisionInternal UpdateAttachment(string filename, BlobStoreWriter body, string contentType, AttachmentEncoding encoding, string docID, string oldRevID)
        {
            if(StringEx.IsNullOrWhiteSpace(filename) || (body != null && contentType == null) || 
                (oldRevID != null && docID == null) || (body != null && docID == null)) {
                throw new CouchbaseLiteException(StatusCode.BadAttachment);
            }

            var oldRev = new RevisionInternal(docID, oldRevID, false);
            if (oldRevID != null) {
                // Load existing revision if this is a replacement:
                try {
                    oldRev = LoadRevisionBody(oldRev);
                } catch (CouchbaseLiteException e) {
                    if (e.Code == StatusCode.NotFound && GetDocument(docID, null, false) != null) {
                        throw new CouchbaseLiteException(StatusCode.Conflict);
                    }

                    throw;
                }
            } else {
                // If this creates a new doc, it needs a body:
                oldRev.SetBody(new Body(new Dictionary<string, object>()));
            }

            // Update the _attachments dictionary:
            var attachments = oldRev.GetProperties().Get("_attachments").AsDictionary<string, object>();
            if (attachments == null) {
                attachments = new Dictionary<string, object>();
            }

            if (body != null) {
                var key = body.GetBlobKey();
                string digest = key.Base64Digest();
                RememberAttachmentWriter(body);
                string encodingName = (encoding == AttachmentEncoding.GZIP) ? "gzip" : null;
                attachments[filename] = new NonNullDictionary<string, object> {
                    { "digest", digest },
                    { "length", body.GetLength() },
                    { "follows", true },
                    { "content_type", contentType },
                    { "encoding", encodingName }
                };
            } else {
                if (oldRevID != null && attachments.Get(filename) == null) {
                    throw new CouchbaseLiteException(StatusCode.AttachmentNotFound);
                }

                attachments.Remove(filename);
            }

            var properties = oldRev.GetProperties();
            properties["_attachments"] = attachments;
            oldRev.SetProperties(properties);

            var newRev = PutRevision(oldRev, oldRevID, false);
            return newRev;
        }

        /// <summary>VALIDATION</summary>
        /// <exception cref="Couchbase.Lite.CouchbaseLiteException"></exception>
        internal Status ValidateRevision(RevisionInternal newRev, RevisionInternal oldRev, String parentRevId)
        {
            var validations = Shared.GetValues("validation", Name);
            if (validations == null || validations.Count == 0) {
                return new Status(StatusCode.Ok);
            }

            var publicRev = new SavedRevision(this, newRev, parentRevId);
            var context = new ValidationContext(this, oldRev, newRev);
            Status status = new Status(StatusCode.Ok);
            foreach (var validationName in validations.Keys)
            {
                var validation = GetValidation(validationName);
                try {
                    validation(publicRev, context);
                } catch(Exception e) {
                    Log.E(TAG, String.Format("Validation block '{0}'", validationName), e);
                    status.Code = StatusCode.Exception;
                    break;
                }
                    
                if (context.RejectMessage != null) {
                    Log.D(TAG, "Failed update of {0}: {1}:{2} Old doc = {3}{2} New doc = {4}", oldRev, context.RejectMessage,
                            Environment.NewLine, oldRev == null ? null : oldRev.GetProperties(), newRev.GetProperties());
                    status.Code = StatusCode.Forbidden;
                    break;
                }
            }

            return status;
        }

        internal String AttachmentStorePath 
        {
            get {
                return Path.Combine(DbDirectory, "attachments");
            }
        }

        internal Task Close()
        {
            if (_closingTask != null) {
                return _closingTask;
            } else if (!IsOpen) {
                return Task.FromResult(true);
            }

            IsOpen = false;
                
            var tcs = new TaskCompletionSource<bool>();
            _closingTask = tcs.Task;
            var retVal = _closingTask; // Will be nulled later

            Log.D(TAG, "Closing database at {0}", DbDirectory);
            if (_views != null) {
                foreach (var view in _views) {
                    view.Value.Close();
                }
            }
               
            var activeReplicatorCopy = ActiveReplicators;
            ActiveReplicators = null;
            if (activeReplicatorCopy != null && activeReplicatorCopy.Count > 0) {
                // Give a chance for replicators to clean up before closing the DB
                var evt = new CountdownEvent(activeReplicatorCopy.Count);
                foreach (var repl in activeReplicatorCopy) {
                    repl.DatabaseClosing(evt);
                }

                ThreadPool.RegisterWaitForSingleObject(evt.WaitHandle, (state, timedOut) =>
                {
                    CloseStorage();
                    tcs.SetResult(!timedOut);
                }, null, 15000, true);
            } else {
                CloseStorage();
                tcs.SetResult(true);
            }

            return retVal;
        }

        internal void CloseStorage()
        {
            try {
                Storage.Close();
            } catch (CouchbaseLiteException) {
                Log.E(TAG, "Failed to close database");
                throw;
            } catch (Exception e) {
                throw new CouchbaseLiteException("Error closing database", e) { Code = StatusCode.Exception };
            } finally {
                Storage = null;

                UnsavedRevisionDocumentCache.Clear();
                DocumentCache = null;
                Manager.ForgetDatabase(this);
                _closingTask = null;
            }
        }

        internal void OpenWithOptions(DatabaseOptions options)
        {
            if (IsOpen) {
                return;
            }

            Log.D(TAG, "Opening {0}", Name);
            _readonly = _readonly || options.ReadOnly;

            // Instantiate storage:
            string storageType = options.StorageType ?? Manager.StorageType ?? DatabaseOptions.SQLITE_STORAGE;

            var className = String.Format("Couchbase.Lite.Store.{0}CouchStore", storageType);
            var primaryStorage = Type.GetType(className, false, true);
            var errorMessage = default(string);
            if (primaryStorage == null) {
                #if !FORESTDB
                if (storageType == DatabaseOptions.FORESTDB_STORAGE) {
                    errorMessage = "ForestDB storage option selected but not compiled into library";
                }
                #endif
                #if NOSQLITE
                if (storageType == DatabaseOptions.SQLITE_STORAGE) {
                    errorMessage = "SQLite storage option selected but not compiled into library";
                }
                #endif
            } else if (primaryStorage.GetInterface("Couchbase.Lite.Store.ICouchStore") == null) {
                errorMessage = String.Format("{0} does not implement ICouchStore", className);
                primaryStorage = null;
            }

            if (primaryStorage == null) {
                throw new CouchbaseLiteException(errorMessage, StatusCode.InvalidStorageType);
            }

            var upgrade = false;
            var primarySQLite = storageType == DatabaseOptions.SQLITE_STORAGE;
            var otherStorage = primarySQLite ? Type.GetType("Couchbase.Lite.Store.ForestDBCouchStore") : 
                Type.GetType("Couchbase.Lite.Store.SqliteCouchStore");


            var primaryStorageInstance = (ICouchStore)Activator.CreateInstance(primaryStorage);
            var otherStorageInstance = otherStorage != null ? (ICouchStore)Activator.CreateInstance(otherStorage) : null;
            if(options.StorageType != null) {
                // If explicit storage type given in options, always use primary storage type,
                // and if secondary db exists, try to upgrade from it:
                upgrade = otherStorageInstance != null && otherStorageInstance.DatabaseExistsIn(DbDirectory) &&
                    !primaryStorageInstance.DatabaseExistsIn(DbDirectory);

                if (upgrade && primarySQLite) {
                    throw new CouchbaseLiteException("Cannot upgrade to SQLite", StatusCode.InvalidStorageType);
                }
            } else {
                // If options don't specify, use primary unless secondary db already exists in dir:
                if (otherStorageInstance != null && otherStorageInstance.DatabaseExistsIn(DbDirectory)) {
                    primaryStorageInstance = otherStorageInstance;
                }
            }

            Log.I(TAG, "Using {0} for db at {1}; upgrade={2}", primaryStorage, DbDirectory, upgrade);
            Storage = primaryStorageInstance;
            Storage.Delegate = this;
            Storage.AutoCompact = AUTO_COMPACT;

            // Encryption:
            var encryptionKey = options.EncryptionKey;
            if (encryptionKey != null) {
                Storage.SetEncryptionKey(encryptionKey);
            }

            // Open the storage!
            try {
                Storage.Open(DbDirectory, Manager, _readonly);
            } catch(CouchbaseLiteException) {
                Storage.Close();
                Log.E(TAG, "Failed to open storage for database");
                throw;
            } catch(DllNotFoundException) {
                throw new CouchbaseLiteException("CBForest native components not found, make sure to install the proper Nuget packages");
            } catch(Exception e) {
                Storage.Close();
                throw new CouchbaseLiteException("Error opening storage for database", e);
            }

            // First-time setup:
            if (PrivateUUID() == null) {
                Storage.SetInfo("privateUUID", Misc.CreateGUID());
                Storage.SetInfo("publicUUID", Misc.CreateGUID());
            }

            var savedMaxRevDepth = _maxRevTreeDepth != 0 ? _maxRevTreeDepth.ToString() : Storage.GetInfo("max_revs");
            int maxRevTreeDepth = 0;
            if (savedMaxRevDepth != null && int.TryParse(savedMaxRevDepth, out maxRevTreeDepth)) {
                SetMaxRevTreeDepth(maxRevTreeDepth);
            } else {
                SetMaxRevTreeDepth(DEFAULT_MAX_REVS);
            }

            // Open attachment store:
            string attachmentsPath = AttachmentStorePath;

            try {
                Attachments = new BlobStore(attachmentsPath, encryptionKey);
            } catch(CouchbaseLiteException) {
                Log.E(TAG, "Error creating blob store at {0}", attachmentsPath);
                Storage.Close();
                Storage = null;
                throw;
            } catch(Exception e) {
                Storage.Close();
                Storage = null;
                throw new CouchbaseLiteException(String.Format("Unknown error creating blob store at {0}", attachmentsPath),
                    e) { Code = StatusCode.Exception };
            }

            IsOpen = true;

            if (upgrade) {
                var upgrader = DatabaseUpgraderFactory.CreateUpgrader(this, DbDirectory);
                try {
                    upgrader.Import();
                } catch(CouchbaseLiteException e) {
                    Log.W(TAG, "Upgrade failed for {0} (Status {1})", DbDirectory, e.CBLStatus);
                    upgrader.Backout();
                    Close();
                    throw;
                }
            }
        }

        internal void Open()
        {
            OpenWithOptions(Manager.DefaultOptionsFor(Name));
        }


        #endregion

        #region Private Methods

        private static long SmallestLength(IDictionary<string, object> attachment)
        {
            long length = attachment.GetCast<long>("length");
            long encodedLength = attachment.GetCast<long>("encoded_length", -1);
            if (encodedLength != -1) {
                length = encodedLength;
            }

            return length;
        }

        private RevisionInternal GetDocumentWithIDAndRev(string docId, string revId, bool withBody)
        {
            return Storage.GetDocument(docId, revId, withBody);
        }

        // Deletes obsolete attachments from the sqliteDb and blob store.
        private bool GarbageCollectAttachments()
        {
            Log.D(TAG, "Scanning database revisions for attachments...");
            var keys = Storage.FindAllAttachmentKeys();
            if (keys == null) {
                return false;
            }

            Log.D(TAG, "...found {0} attachments", keys.Count);
            var numDeleted = Attachments.DeleteBlobsExceptWithKeys(keys);
            Log.D(TAG, "    ... deleted {0} obsolete attachment files.", numDeleted);

            return numDeleted >= 0;
        }

        private string CheckpointInfoKey(string checkpointId) 
        {
            return "checkpoint/" + checkpointId;
        }

        private long KeyToSequence(object key, long defaultVal)
        {
            if (key == null) {
                return defaultVal;
            }

            try {
                return Convert.ToInt64(key);
            } catch(Exception) {
                return defaultVal;
            }
        }

        private Document GetDocument(string docId, bool mustExist)
        {
            if (StringEx.IsNullOrWhiteSpace (docId)) {
                return null;
            }

            var unsavedDoc = default(WeakReference);
            var success = UnsavedRevisionDocumentCache.TryGetValue(docId, out unsavedDoc);
            var doc = success
                ? (Document)unsavedDoc.Target 
                : DocumentCache.Get(docId);

            if (doc != null) {
                if (mustExist && doc.CurrentRevision == null) {
                    return null;
                }

                return doc;
            }

            doc = new Document(this, docId);
            if (mustExist && doc.CurrentRevision == null) {
                return null;
            }

            if (DocumentCache == null) {
                DocumentCache = new LruCache<string, Document>(MAX_DOC_CACHE_SIZE);
            }

            DocumentCache[docId] = doc;
            UnsavedRevisionDocumentCache.TryAdd(docId, new WeakReference(doc));
            return doc;
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// Releases all resource used by the <see cref="Couchbase.Lite.Database"/> object.
        /// </summary>
        /// <remarks>
        /// The database file may be used again if open is called
        /// </remarks>
        public void Dispose()
        {
            if (IsOpen) {
                try {
                    Close();
                } catch(Exception) {
                    Log.E(TAG, "Error disposing database (possibly already disposed?)");
                }
            }
        }
    
        #endregion

        #region ICouchStoreDelegate
        #pragma warning disable 1591

        public void StorageExitedTransaction(bool committed)
        {
            var changes = _changesToNotify;
            if (!committed && changes != null) {
                // I already told cached Documents about these new revisions. Back that out:
                foreach(var change in changes) {
                    var doc = DocumentCache.Get(change.DocumentId);
                    if (doc != null) {
                        doc.ForgetCurrentRevision();
                    }
                }

                _changesToNotify = null;
            }

            PostChangeNotifications();
        }

        public void DatabaseStorageChanged(DocumentChange change)
        {
            Log.D(TAG, "Added: {0}", change.AddedRevision);
            if (_changesToNotify == null) {
                _changesToNotify = new List<DocumentChange>();
            }

            _changesToNotify.Add(change);
            if (!PostChangeNotifications()) {
                // The notification wasn't posted yet, probably because a transaction is open.
                // But the Document, if any, needs to know right away so it can update its
                // currentRevision.
                var doc = DocumentCache.Get(change.DocumentId);
                if (doc != null) {
                    doc.RevisionAdded(change, false);
                }
            }

            // Squish the change objects if too many of them are piling up
            if (_changesToNotify.Count >= NOTIFY_CHANGES_LIMIT) {
                if (_changesToNotify.Count == NOTIFY_CHANGES_LIMIT) {
                    foreach (var c in _changesToNotify) {
                        c.ReduceMemoryUsage();
                    }
                } else {
                    change.ReduceMemoryUsage();
                }
            }
        }

        public string GenerateRevID(IEnumerable<byte> json, bool deleted, string previousRevisionId)
        {
            MessageDigest md5Digest;

            // Revision IDs have a generation count, a hyphen, and a UUID.
            int generation = 0;
            if (previousRevisionId != null) {
                generation = RevisionInternal.GenerationFromRevID(previousRevisionId);
                if (generation == 0) {
                    return null;
                }
            }

            // Generate a digest for this revision based on the previous revision ID, document JSON,
            // and attachment digests. This doesn't need to be secure; we just need to ensure that this
            // code consistently generates the same ID given equivalent revisions.
            try {
                md5Digest = MessageDigest.GetInstance("MD5");
            } catch (NoSuchAlgorithmException e) {
                throw new RuntimeException(e);
            }

            var length = 0;
            if (previousRevisionId != null) {
                var prevIDUTF8 = Encoding.UTF8.GetBytes(previousRevisionId);
                length = prevIDUTF8.Length;
                if (length > unchecked((0xFF))) {
                    return null;
                }

                var lengthByte = unchecked((byte)(length & unchecked((0xFF))));
                md5Digest.Update(lengthByte);
                md5Digest.Update(prevIDUTF8);
            }



            var isDeleted = deleted ? 1 : 0;
            var deletedByte = new[] { unchecked((byte)isDeleted) };
            md5Digest.Update(deletedByte);

            if (json != null)
            {
                md5Digest.Update(json != null ? json.ToArray() : null);
            }

            var md5DigestResult = md5Digest.Digest();
            var digestAsHex = BitConverter.ToString(md5DigestResult).Replace("-", String.Empty);
            int generationIncremented = generation + 1;
            return string.Format("{0}-{1}", generationIncremented, digestAsHex).ToLower();
        }

        #pragma warning restore 1591
        #endregion
    }

    #region Global Delegates

    /// <summary>
    /// A delegate that can validate a key/value change.
    /// </summary>
    public delegate Boolean ValidateChangeDelegate(String key, Object oldValue, Object newValue);

    /// <summary>
    /// A delegate that can be run asynchronously on a <see cref="Couchbase.Lite.Database"/>.
    /// </summary>
    public delegate void RunAsyncDelegate(Database database);

    /// <summary>
    /// A delegate that can be used to accept/reject new <see cref="Couchbase.Lite.Revision"/>s being added to a <see cref="Couchbase.Lite.Database"/>.
    /// </summary>
    public delegate Boolean ValidateDelegate(Revision newRevision, IValidationContext context);

    /// <summary>
    /// A delegate that can be used to include/exclude <see cref="Couchbase.Lite.Revision"/>s during push <see cref="Couchbase.Lite.Replication"/>.
    /// </summary>
    public delegate Boolean FilterDelegate(SavedRevision revision, IDictionary<String, Object> filterParams);

    /// <summary>
    /// A delegate that can be run in a transaction on a <see cref="Couchbase.Lite.Database"/>.
    /// </summary>
    public delegate Boolean RunInTransactionDelegate();

    ///
    /// <summary>The event raised when a <see cref="Couchbase.Lite.Database"/> changes</summary>
    ///
    public class DatabaseChangeEventArgs : EventArgs 
    {
        /// <summary>
        /// Gets the <see cref="Couchbase.Lite.Database"/> that raised the event.
        /// </summary>
        /// <value>The <see cref="Couchbase.Lite.Database"/> that raised the event.</value>
            public Database Source { get; internal set; }

        /// <summary>
        /// Returns true if the change was not made by a Document belonging to this Database 
        /// (e.g. it came from another process or from a pull Replication), otherwise false.
        /// </summary>
        /// <value>true if the change was not made by a Document belonging to this Database 
        /// (e.g. it came from another process or from a pull Replication), otherwise false</value>
            public Boolean IsExternal { get; internal set; }

        /// <summary>
        /// Gets the DocumentChange details for the Documents that caused the Database change.
        /// </summary>
        /// <value>The DocumentChange details for the Documents that caused the Database change.</value>
            public IEnumerable<DocumentChange> Changes { get; internal set; }
    }

    #endregion

    #region IFilterCompiler

    /// <summary>
    /// An interface for compiling filters on queries in languages other than C#
    /// </summary>
    public interface IFilterCompiler
    {
        /// <summary>
        /// Compiles the filter.
        /// </summary>
        /// <returns>The compiled filter.</returns>
        /// <param name="filterSource">The filter source code</param>
        /// <param name="language">The language that the source was written in</param>
        FilterDelegate CompileFilter(string filterSource, string language);
    }

    #endregion
}

