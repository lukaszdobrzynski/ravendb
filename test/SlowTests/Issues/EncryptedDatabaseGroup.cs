﻿using System;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Graph;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Raven.Server.ServerWide.Context;
using Tests.Infrastructure;
using Xunit;

namespace SlowTests.Issues
{
    public class EncryptedDatabaseGroup: ClusterTestBase
    {
        [Fact]
        public async Task AddingNodeToEncryptedDatabaseGroupShouldThrow()
        {
            var (nodes, leader) = await CreateRaftClusterWithSsl(3);            

            var options = new Options
            {
                Server = leader,
                ReplicationFactor = 2,
                Encrypted = true
            };

            using (var store = GetDocumentStore(options))
            {
                var res = await store.Maintenance.Server.SendAsync(new AddDatabaseNodeOperation(store.Database));
                await WaitForRaftIndexToBeAppliedInCluster(res.RaftCommandIndex, TimeSpan.FromSeconds(15));
                var notInDbGroupServer = Servers.Single(s => res.Topology.Members.Contains(s.ServerStore.NodeTag) == false);
                var dbName = store.Database;
                using (var notInDbGroupStore = GetDocumentStore(new Options
                {
                    Server = notInDbGroupServer,
                    CreateDatabase = false,
                    ModifyDocumentStore = ds => ds.Conventions.DisableTopologyUpdates = true,
                    ClientCertificate = options.ClientCertificate,
                    ModifyDatabaseName = _ => dbName
                }))
                {
                    await Assert.ThrowsAsync<Raven.Client.Exceptions.Database.DatabaseLoadFailureException>(async ()=> await TrySavingDocument(notInDbGroupStore));
                    PutSecrectKeyForDatabaseInServersStore(dbName, notInDbGroupServer);
                    await notInDbGroupServer.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(dbName, true);
                    await TrySavingDocument(notInDbGroupStore);
                }                
            }
        }

        private static async Task TrySavingDocument(DocumentStore store)
        {
            using (var session = store.OpenAsyncSession())
            {
                await session.StoreAsync(new User {Name = "Foo"});
                await session.SaveChangesAsync();
            }
        }

        [Fact]
        public async Task DeletingMasterKeyForExistedEncryptedDatabaseShouldFail()
        {
            var (nodes, server) = await CreateRaftClusterWithSsl(1);

            var options = new Options
            {
                Server = server,
                Encrypted = true
            };
            using (var store = GetDocumentStore(options))
            {
                await TrySavingDocument(store);
                using (server.ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext ctx))                
                {
                    using (ctx.OpenWriteTransaction())
                    {
                        Assert.Throws<InvalidOperationException>(() => server.ServerStore.DeleteSecretKey(ctx, store.Database));
                    }

                    store.Maintenance.Server.Send(new DeleteDatabasesOperation(store.Database, true));
                    using (ctx.OpenWriteTransaction())
                    {
                        server.ServerStore.DeleteSecretKey(ctx, store.Database);
                    }
                }
            }
        }

        [Fact]
        public async Task DeletingEncryptedDatabaseFromDatabaseGroup()
        {
            var (nodes, server) = await CreateRaftClusterWithSsl(3);

            var options = new Options
            {
                Server = server,
                ReplicationFactor = 3,
                Encrypted = true
            };

            using (var store = GetDocumentStore(options))
            {
                await TrySavingDocument(store);
                store.Maintenance.Server.Send(new DeleteDatabasesOperation(store.Database, true, fromNode: nodes[0].ServerStore.NodeTag));
                var value = WaitForValue(() => GetTopology().AllNodes.Count(), 2);
                Assert.Equal(2, value);

                store.Maintenance.Server.Send(new DeleteDatabasesOperation(store.Database, true, fromNode: nodes[1].ServerStore.NodeTag));
                value = WaitForValue(() => GetTopology().AllNodes.Count(), 1);
                Assert.Equal(1, value);

                DatabaseTopology GetTopology()
                {
                    var serverStore = nodes[2].ServerStore;
                    using (serverStore.ContextPool.AllocateOperationContext(out TransactionOperationContext ctx))
                    using (ctx.OpenReadTransaction())
                    {
                        var databaseRecord = serverStore.Cluster.ReadRawDatabaseRecord(ctx, store.Database);
                        return databaseRecord.GetTopology();
                    }
                }
            }
        }
    }
}
