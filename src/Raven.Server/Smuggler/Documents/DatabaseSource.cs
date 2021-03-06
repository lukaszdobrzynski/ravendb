﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Raven.Client;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Counters;
using Raven.Client.Documents.Smuggler;
using Raven.Client.Documents.Subscriptions;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations.Configuration;
using Raven.Client.Util;
using Raven.Server.Documents;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Raven.Server.Smuggler.Documents.Data;
using Sparrow.Json;
using Voron;

namespace Raven.Server.Smuggler.Documents
{
    public class DatabaseSource : ISmugglerSource
    {
        private readonly DocumentDatabase _database;
        private DocumentsOperationContext _context;
        private TransactionOperationContext _serverContext;

        private readonly long _startDocumentEtag;
        private readonly long _startRaftIndex;
        private IDisposable _returnContext;
        private IDisposable _returnServerContext;
        private DocumentsTransaction _disposeTransaction;
        private IDisposable _disposeServerTransaction;

        private int _currentTypeIndex;

        private readonly DatabaseItemType[] _types =
        {
            DatabaseItemType.DatabaseRecord,
            DatabaseItemType.Documents,
            DatabaseItemType.RevisionDocuments,
            DatabaseItemType.Tombstones,
            DatabaseItemType.Conflicts,
            DatabaseItemType.Indexes,
            DatabaseItemType.Identities,
            DatabaseItemType.CompareExchange,
            DatabaseItemType.CompareExchangeTombstones,
            DatabaseItemType.CounterGroups,
            DatabaseItemType.Subscriptions,
            DatabaseItemType.None
        };

        public long LastEtag { get; private set; }
        public long LastRaftIndex { get; private set; }

        public DatabaseSource(DocumentDatabase database, long startDocumentEtag, long startRaftIndex)
        {
            _database = database;
            _startDocumentEtag = startDocumentEtag;
            _startRaftIndex = startRaftIndex;
        }

        public IDisposable Initialize(DatabaseSmugglerOptions options, SmugglerResult result, out long buildVersion)
        {
            _currentTypeIndex = 0;

            if (options.OperateOnTypes.HasFlag(DatabaseItemType.Documents) ||
                options.OperateOnTypes.HasFlag(DatabaseItemType.RevisionDocuments) ||
                options.OperateOnTypes.HasFlag(DatabaseItemType.Tombstones) ||
                options.OperateOnTypes.HasFlag(DatabaseItemType.Conflicts) ||
                options.OperateOnTypes.HasFlag(DatabaseItemType.CounterGroups))
            {
                _returnContext = _database.DocumentsStorage.ContextPool.AllocateOperationContext(out _context);
                _disposeTransaction = _context.OpenReadTransaction();
                LastEtag = DocumentsStorage.ReadLastEtag(_disposeTransaction.InnerTransaction);
            }

            if (options.OperateOnTypes.HasFlag(DatabaseItemType.CompareExchange) ||
                options.OperateOnTypes.HasFlag(DatabaseItemType.Identities) ||
                options.OperateOnTypes.HasFlag(DatabaseItemType.CompareExchangeTombstones))
            {
                _returnServerContext = _database.ServerStore.ContextPool.AllocateOperationContext(out _serverContext);
                _disposeServerTransaction = _serverContext.OpenReadTransaction();

                using (var rawRecord = _database.ServerStore.Cluster.ReadRawDatabaseRecord(_serverContext, _database.Name))
                {
                    LastRaftIndex = rawRecord.GetEtagForBackup();
                }
            }

            buildVersion = ServerVersion.Build;
            return new DisposableAction(() =>
            {
                _disposeServerTransaction?.Dispose();
                _returnServerContext?.Dispose();

                _disposeTransaction?.Dispose();
                _returnContext?.Dispose();
            });
        }

        public DatabaseItemType GetNextType()
        {
            return _types[_currentTypeIndex++];
        }

        public DatabaseRecord GetDatabaseRecord()
        {
            var databaseRecord = _database.ReadDatabaseRecord();

            // filter server-wide backup tasks;
            for (var i = databaseRecord.PeriodicBackups.Count - 1; i >= 0; i--)
            {
                var periodicBackup = databaseRecord.PeriodicBackups[i];
                if (periodicBackup.Name == null)
                    continue;

                if (periodicBackup.Name.StartsWith(ServerWideBackupConfiguration.NamePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    databaseRecord.PeriodicBackups.RemoveAt(i);
                }
            }

            return databaseRecord;
        }

        public IEnumerable<DocumentItem> GetDocuments(List<string> collectionsToExport, INewDocumentActions actions)
        {
            Debug.Assert(_context != null);

            var documents = collectionsToExport.Count != 0
                ? _database.DocumentsStorage.GetDocumentsFrom(_context, collectionsToExport, _startDocumentEtag, int.MaxValue)
                : _database.DocumentsStorage.GetDocumentsFrom(_context, _startDocumentEtag, 0, int.MaxValue);

            foreach (var document in documents)
            {
                yield return new DocumentItem
                {
                    Document = document
                };
            }
        }

        public IEnumerable<DocumentItem> GetRevisionDocuments(List<string> collectionsToExport, INewDocumentActions actions)
        {
            Debug.Assert(_context != null);

            var revisionsStorage = _database.DocumentsStorage.RevisionsStorage;
            if (revisionsStorage.Configuration == null)
                yield break;

            var documents = collectionsToExport.Count != 0
                ? revisionsStorage.GetRevisionsFrom(_context, collectionsToExport, _startDocumentEtag, int.MaxValue)
                : revisionsStorage.GetRevisionsFrom(_context, _startDocumentEtag, int.MaxValue);

            foreach (var document in documents)
            {
                yield return new DocumentItem
                {
                    Document = document
                };
            }
        }

        public IEnumerable<DocumentItem> GetLegacyAttachments(INewDocumentActions actions)
        {
            return Enumerable.Empty<DocumentItem>();
        }

        public IEnumerable<string> GetLegacyAttachmentDeletions()
        {
            return Enumerable.Empty<string>();
        }

        public IEnumerable<string> GetLegacyDocumentDeletions()
        {
            return Enumerable.Empty<string>();
        }

        public Stream GetAttachmentStream(LazyStringValue hash, out string tag)
        {
            Debug.Assert(_context != null);

            using (Slice.External(_context.Allocator, hash, out Slice hashSlice))
            {
                return _database.DocumentsStorage.AttachmentsStorage.GetAttachmentStream(_context, hashSlice, out tag);
            }
        }

        public IEnumerable<Tombstone> GetTombstones(List<string> collectionsToExport, INewDocumentActions actions)
        {
            Debug.Assert(_context != null);

            var tombstones = collectionsToExport.Count > 0
                ? _database.DocumentsStorage.GetTombstonesFrom(_context, collectionsToExport, _startDocumentEtag, int.MaxValue)
                : _database.DocumentsStorage.GetTombstonesFrom(_context, _startDocumentEtag, 0, int.MaxValue);
            
            foreach (var tombstone in tombstones)
            {
                yield return tombstone;
            }
        }

        public IEnumerable<DocumentConflict> GetConflicts(List<string> collectionsToExport, INewDocumentActions actions)
        {
            Debug.Assert(_context != null);

            var conflicts = _database.DocumentsStorage.ConflictsStorage.GetConflictsFrom(_context, _startDocumentEtag);

            if (collectionsToExport.Count > 0)
            {
                foreach (var conflict in conflicts)
                {
                    if (collectionsToExport.Contains(conflict.Collection) == false)
                        continue;

                    yield return conflict;
                }

                yield break;
            }

            foreach (var conflict in conflicts)
            {
                yield return conflict;
            }
        }

        public IEnumerable<IndexDefinitionAndType> GetIndexes()
        {
            var allIndexes = _database.IndexStore.GetIndexes().ToList();
            var sideBySideIndexes = allIndexes.Where(x => x.Name.StartsWith(Constants.Documents.Indexing.SideBySideIndexNamePrefix)).ToList();

            var originalSideBySideIndexNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var index in sideBySideIndexes)
            {
                allIndexes.Remove(index);

                if (index.Type == IndexType.Faulty)
                    continue;

                var indexName = index.Name.Remove(0, Constants.Documents.Indexing.SideBySideIndexNamePrefix.Length);
                originalSideBySideIndexNames.Add(indexName);
                var indexDefinition = index.GetIndexDefinition();
                indexDefinition.Name = indexName;

                yield return new IndexDefinitionAndType
                {
                    IndexDefinition = indexDefinition,
                    Type = index.Type
                };
            }

            foreach (var index in allIndexes)
            {
                if (originalSideBySideIndexNames.Contains(index.Name))
                    continue;

                if (index.Type == IndexType.Faulty)
                    continue;

                if (index.Type.IsStatic())
                {
                    yield return new IndexDefinitionAndType
                    {
                        IndexDefinition = index.GetIndexDefinition(),
                        Type = index.Type
                    };

                    continue;
                }

                yield return new IndexDefinitionAndType
                {
                    IndexDefinition = index.Definition,
                    Type = index.Type
                };
            }
        }

        public IEnumerable<(string Prefix, long Value, long Index)> GetIdentities()
        {
            Debug.Assert(_serverContext != null);

            return _database.ServerStore.Cluster.GetIdentitiesFromPrefix(_serverContext, _database.Name, _startRaftIndex, long.MaxValue);
        }

        public IEnumerable<(string key, long index, BlittableJsonReaderObject value)> GetCompareExchangeValues()
        {
            Debug.Assert(_serverContext != null);

            return _database.ServerStore.Cluster.GetCompareExchangeFromPrefix(_serverContext, _database.Name, _startRaftIndex, int.MaxValue);
        }

        public IEnumerable<string> GetCompareExchangeTombstones()
        {
            Debug.Assert(_serverContext != null);

            return _database.ServerStore.Cluster.GetCompareExchangeTombstonesByKey(_serverContext, _database.Name);
        }
        public IEnumerable<CounterGroupDetail> GetCounterValues(List<string> collectionsToExport, ICounterActions actions)
        {
            Debug.Assert(_context != null);

            if (collectionsToExport?.Count > 0)
            {
                foreach (var collection in collectionsToExport)
                {
                    foreach (var counter in _database.DocumentsStorage.CountersStorage.GetCountersFrom(_context, collection, _startDocumentEtag, 0, int.MaxValue))
                    {
                        yield return counter;
                    }
                }

                yield break;
            }

            foreach (var counter in _database.DocumentsStorage.CountersStorage.GetCountersFrom(_context, _startDocumentEtag, 0, int.MaxValue))
            {
                yield return counter;
            }
        } 

        public IEnumerable<CounterDetail> GetLegacyCounterValues()
        {
            // used only in StreamSource
            return Enumerable.Empty<CounterDetail>();
        }

        public IEnumerable<SubscriptionState> GetSubscriptions()
        {
            Debug.Assert(_context != null);

            return _database.SubscriptionStorage.GetAllSubscriptions(_serverContext, false, 0, int.MaxValue);
        }

        public long SkipType(DatabaseItemType type, Action<long> onSkipped, CancellationToken token)
        {
            return 0; // no-op
        }
    }
}
