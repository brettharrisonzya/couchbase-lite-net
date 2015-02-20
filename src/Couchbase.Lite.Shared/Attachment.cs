//
// Attachment.cs
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

using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using Couchbase.Lite.Internal;
using Sharpen;

namespace Couchbase.Lite {

    struct AttachmentMetadataKeys {
        internal static readonly String ContentType = "content_type";
        internal static readonly String Length = "length";
        internal static readonly String Follows = "follows";
        internal static readonly String Digest = "digest";
    }

    /// <summary>
    /// A Couchbase Lite Document Attachment.
    /// </summary>
    public sealed class Attachment : IDisposable {

        #region Constants

        internal const int DefaultStreamChunkSize = 8192;

        #endregion

        #region Constructors

        internal Attachment(Stream contentStream, string contentType)
        {
            Metadata = new Dictionary<String, Object> {
                { AttachmentMetadataKeys.ContentType, contentType },
                { AttachmentMetadataKeys.Follows, true }
            };

            Body = contentStream;
        }

        internal Attachment(Revision revision, String name, IDictionary<String, Object> metadata)
        {
            Revision = revision;
            Name = name;
            Metadata = metadata;
        }

        internal Attachment(string name, IDictionary<String, Object> metadata)
        {
            Name = name;
            Metadata = metadata;
        }

        #endregion

        #region Non-Public Instance Members

        internal Stream Body { get; set; }

        /// <summary>
        /// Goes through an _attachments dictionary and replaces any values that are Attachment objects
        /// with proper JSON metadata dicts.
        /// </summary>
        /// <remarks>
        /// Goes through an _attachments dictionary and replaces any values that are Attachment objects
        /// with proper JSON metadata dicts. It registers the attachment bodies with the blob store and sets
        /// the metadata 'digest' and 'follows' properties accordingly.
        /// </remarks>
        internal static IDictionary<string, object> InstallAttachmentBodies(IDictionary<String, Object> attachments, Database database)
        {
            var updatedAttachments = new Dictionary<string, object>();
            foreach (string name in attachments.Keys)
            {
                object value;
                attachments.TryGetValue(name, out value);

                if (value is Attachment)
                {
                    var attachment = (Attachment)value;
                    var metadataMutable = new Dictionary<string, object>(attachment.Metadata);
                    var body = attachment.Body;
                    if (body != null)
                    {
                        // Copy attachment body into the database's blob store:
                        var writer = BlobStoreWriterForBody(body, database);
                        metadataMutable["length"] = (long)writer.GetLength();
                        metadataMutable["digest"] = writer.MD5DigestString();
                        metadataMutable["follows"] = true;
                        database.RememberAttachmentWriter(writer);
                    }
                    updatedAttachments[name] = metadataMutable;
                }
                else if (value is AttachmentInternal)
                {
                    throw new ArgumentException("AttachmentInternal objects not expected here.  Could indicate a bug");
                }
                else 
                {
                    if (value != null)
                        updatedAttachments[name] = value;
                }
            }
            return updatedAttachments;
        }

        internal static BlobStoreWriter BlobStoreWriterForBody(Stream body, Database database)
        {
            var writer = database.AttachmentWriter;
            writer.Read(body);
            writer.Finish();
            return writer;
        }

        #endregion

        #region Instance Members

        internal Database Database { get; set; }

        /// <summary>
        /// Gets the owning <see cref="Couchbase.Lite.Revision"/>.
        /// </summary>
        /// <value>the owning <see cref="Couchbase.Lite.Revision"/>.</value>
        public Revision Revision { get; internal set; }

        /// <summary>
        /// Gets the owning <see cref="Couchbase.Lite.Document"/>.
        /// </summary>
        /// <value>The owning <see cref="Couchbase.Lite.Document"/></value>
        /// <exception cref="Couchbase.Lite.CouchbaseLiteException"></exception>
        public Document Document {
            get {
                if (Revision == null)
                    throw new CouchbaseLiteException("Revision must not be null.");
                return Revision.Document;
            } 
        }

        /// <summary>
        /// Gets the name of the <see cref="Couchbase.Lite.Attachment"/>.
        /// </summary>
        /// <value>The name of the <see cref="Couchbase.Lite.Attachment"/>.</value>
        public String Name { get ; internal set; }

        /// <summary>
        /// Gets the content-type.
        /// </summary>
        /// <value>The content-type.</value>
        public String ContentType {
            get {
                return Metadata.Get(AttachmentMetadataKeys.ContentType) as String;
            }
        }

        /// <summary>
        /// Get the <see cref="Couchbase.Lite.Attachment"/> content stream.
        /// </summary>
        /// <value>The <see cref="Couchbase.Lite.Attachment"/> content stream.</value>
        /// <exception cref="Couchbase.Lite.CouchbaseLiteException">
        /// Thrown if an error occurs when getting the content stream.
        /// </exception>
        public Stream ContentStream { 
            get {
                BlobKey key = new BlobKey((string)Metadata[AttachmentMetadataKeys.Digest]);
                return Database.Attachments.BlobStreamForKey(key);
            }
        }

        /// <summary>Gets the <see cref="Couchbase.Lite.Attachment"/> content.</summary>
        /// <value>The <see cref="Couchbase.Lite.Attachment"/> content</value>
        /// <exception cref="Couchbase.Lite.CouchbaseLiteException">
        /// Thrown if an error occurs when getting the content.
        /// </exception>
        public IEnumerable<Byte> Content 
        { 
            get {
                ContentStream.Reset();

                var stream = ContentStream;
                var ms = new MemoryStream();

                stream.CopyTo(ms);
                var bytes = ms.ToArray();

                ms.Dispose();
                return bytes;
            }
        }

        /// <summary>
        /// Gets the length in bytes of the content.
        /// </summary>
        /// <value>The length in bytes of the content.</value>
        public Int64 Length {
            get {
                Object length;
                var success = Metadata.TryGetValue(AttachmentMetadataKeys.Length, out length);
                return success ? (Int64)length : 0;
            }
        }

        /// <summary>The CouchbaseLite metadata about the attachment, that lives in the document.
        public IDictionary<String, Object> Metadata { get ; private set; }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (Body != null)
            {
                Body.Dispose();
                Body = null;
            }
        }

        #endregion
    }

}

