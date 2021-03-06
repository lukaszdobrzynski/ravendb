﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Raven.Client.Documents.Conventions;
using Raven.Client.Http;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Commands;
using Raven.Server.Json;
using Raven.Server.Routing;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Raven.Server.Web;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Sparrow.Platform;
using Sparrow.Server.Platform.Posix;

namespace Raven.Server.Documents.Handlers.Debugging
{
    public class ServerWideDebugInfoPackageHandler : RequestHandler
    {
        private static readonly string[] EmptyStringArray = new string[0];

        //this endpoint is intended to be called by /debug/cluster-info-package only
        [RavenAction("/admin/debug/remote-cluster-info-package", "GET", AuthorizationStatus.Operator)]
        public async Task GetClusterWideInfoPackageForRemote()
        {
            var stackTraces = StackTraces();

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext transactionOperationContext))
            using (ServerStore.ContextPool.AllocateOperationContext(out JsonOperationContext jsonOperationContext))
            using (transactionOperationContext.OpenReadTransaction())
            {
                using (var ms = new MemoryStream())
                {
                    using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
                    {
                        var localEndpointClient = new LocalEndpointClient(Server);
                        NodeDebugInfoRequestHeader requestHeader;
                        using (var requestHeaderJson =
                            await transactionOperationContext.ReadForMemoryAsync(HttpContext.Request.Body, "remote-cluster-info-package/read request header"))
                        {
                            requestHeader = JsonDeserializationServer.NodeDebugInfoRequestHeader(requestHeaderJson);
                        }

                        await WriteServerWide(archive, jsonOperationContext, localEndpointClient, stackTraces);
                        foreach (var databaseName in requestHeader.DatabaseNames)
                        {
                            await WriteForDatabase(archive, jsonOperationContext, localEndpointClient, databaseName);
                        }

                    }

                    ms.Position = 0;
                    await ms.CopyToAsync(ResponseBodyStream());
                }
            }
        }

        [RavenAction("/admin/debug/cluster-info-package", "GET", AuthorizationStatus.Operator, IsDebugInformationEndpoint = true)]
        public async Task GetClusterWideInfoPackage()
        {
            var stacktraces = StackTraces();

            var contentDisposition = $"attachment; filename={DateTime.UtcNow:yyyy-MM-dd H:mm:ss} Cluster Wide.zip";

            HttpContext.Response.Headers["Content-Disposition"] = contentDisposition;

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext transactionOperationContext))
            using (ServerStore.ContextPool.AllocateOperationContext(out JsonOperationContext jsonOperationContext))
            using (transactionOperationContext.OpenReadTransaction())
            {
                using (var ms = new MemoryStream())
                {
                    using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
                    {
                        var localEndpointClient = new LocalEndpointClient(Server);

                        using (var localMemoryStream = new MemoryStream())
                        {
                            //assuming that if the name tag is empty
                            var nodeName = $"Node - [{ServerStore.NodeTag ?? "Empty node tag"}]";

                            using (var localArchive = new ZipArchive(localMemoryStream, ZipArchiveMode.Create, true))
                            {
                                await WriteServerWide(localArchive, jsonOperationContext, localEndpointClient, stacktraces);
                                await WriteForAllLocalDatabases(localArchive, jsonOperationContext, localEndpointClient);
                            }

                            localMemoryStream.Position = 0;
                            var entry = archive.CreateEntry($"{nodeName}.zip");
                            entry.ExternalAttributes = ((int)(FilePermissions.S_IRUSR | FilePermissions.S_IWUSR)) << 16;

                            using (var entryStream = entry.Open())
                            {
                                localMemoryStream.CopyTo(entryStream);
                                entryStream.Flush();
                            }
                        }
                        var databaseNames = ServerStore.Cluster.GetDatabaseNames(transactionOperationContext).ToList();
                        var topology = ServerStore.GetClusterTopology(transactionOperationContext);

                        //this means no databases are defined in the cluster
                        //in this case just output server-wide endpoints from all cluster nodes
                        if (databaseNames.Count == 0)
                        {
                            foreach (var tagWithUrl in topology.AllNodes)
                            {
                                if (tagWithUrl.Value.Contains(ServerStore.GetNodeHttpServerUrl()))
                                    continue;

                                try
                                {
                                    await WriteDebugInfoPackageForNodeAsync(
                                        jsonOperationContext,
                                        archive,
                                        tag: tagWithUrl.Key,
                                        url: tagWithUrl.Value,
                                        certificate: Server.Certificate.Certificate,
                                        databaseNames: null,
                                        stacktraces: stacktraces);
                                }
                                catch (Exception e)
                                {
                                    var entryName = $"Node - [{tagWithUrl.Key}]";
                                    DebugInfoPackageUtils.WriteExceptionAsZipEntry(e, archive, entryName);
                                }
                            }
                        }
                        else
                        {
                            var nodeUrlToDatabaseNames = CreateUrlToDatabaseNamesMapping(transactionOperationContext, databaseNames);
                            foreach (var urlToDatabaseNamesMap in nodeUrlToDatabaseNames)
                            {
                                if (urlToDatabaseNamesMap.Key.Contains(ServerStore.GetNodeHttpServerUrl()))
                                    continue; //skip writing local data, we do it separately

                                try
                                {
                                    await WriteDebugInfoPackageForNodeAsync(
                                        jsonOperationContext,
                                        archive,
                                        tag: urlToDatabaseNamesMap.Value.Item2,
                                        url: urlToDatabaseNamesMap.Key,
                                        databaseNames: urlToDatabaseNamesMap.Value.Item1,
                                        certificate: Server.Certificate.Certificate,
                                        stacktraces: stacktraces);
                                }
                                catch (Exception e)
                                {
                                    var entryName = $"Node - [{urlToDatabaseNamesMap.Value.Item2}]";
                                    DebugInfoPackageUtils.WriteExceptionAsZipEntry(e, archive, entryName);
                                }
                            }
                        }
                    }

                    ms.Position = 0;
                    await ms.CopyToAsync(ResponseBodyStream());
                }
            }
        }

        private async Task WriteDebugInfoPackageForNodeAsync(
            JsonOperationContext context,
            ZipArchive archive,
            string tag,
            string url,
            IEnumerable<string> databaseNames,
            X509Certificate2 certificate,
            bool stacktraces)
        {
            //note : theoretically GetDebugInfoFromNodeAsync() can throw, error handling is done at the level of WriteDebugInfoPackageForNodeAsync() calls
            using (var requestExecutor = ClusterRequestExecutor.CreateForSingleNode(url, certificate))
            {
                var timeout = TimeSpan.FromMinutes(1);
                if (ServerStore.Configuration.Cluster.OperationTimeout.AsTimeSpan > timeout)
                    timeout = ServerStore.Configuration.Cluster.OperationTimeout.AsTimeSpan;

                requestExecutor.DefaultTimeout = timeout;

                using (var responseStream = await GetDebugInfoFromNodeAsync(
                    context,
                    requestExecutor,
                    databaseNames ?? EmptyStringArray,
                    stacktraces))
                {
                    var entry = archive.CreateEntry($"Node - [{tag}].zip");
                    entry.ExternalAttributes = ((int)(FilePermissions.S_IRUSR | FilePermissions.S_IWUSR)) << 16;

                    using (var entryStream = entry.Open())
                    {
                        await responseStream.CopyToAsync(entryStream);
                        await entryStream.FlushAsync();
                    }
                }
            }
        }

        [RavenAction("/admin/debug/info-package", "GET", AuthorizationStatus.Operator, IsDebugInformationEndpoint = true)]
        public async Task GetInfoPackage()
        {
            var stacktraces = StackTraces();

            var contentDisposition = $"attachment; filename={DateTime.UtcNow:yyyy-MM-dd H:mm:ss} - Node [{ServerStore.NodeTag}].zip";
            HttpContext.Response.Headers["Content-Disposition"] = contentDisposition;
            using (ServerStore.ContextPool.AllocateOperationContext(out JsonOperationContext context))
            {
                using (var ms = new MemoryStream())
                {
                    using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
                    {
                        var localEndpointClient = new LocalEndpointClient(Server);
                        await WriteServerWide(archive, context, localEndpointClient, stacktraces);
                        await WriteForAllLocalDatabases(archive, context, localEndpointClient);
                    }

                    ms.Position = 0;
                    await ms.CopyToAsync(ResponseBodyStream());
                }
            }
        }

        private static void DumpStackTraces(ZipArchive archive, string prefix)
        {
            var zipArchiveEntry = archive.CreateEntry($"{prefix}/stacktraces.json", CompressionLevel.Optimal);

            var threadsUsage = new ThreadsUsage();
            var sp = Stopwatch.StartNew();

            using (var stackTraceStream = zipArchiveEntry.Open())
            using (var sw = new StringWriter())
            {
                try
                {
                    if (Debugger.IsAttached)
                        throw new InvalidOperationException("Cannot get stack traces when debugger is attached");

                    ThreadsHandler.OutputResultToStream(sw);

                    var result = JObject.Parse(sw.GetStringBuilder().ToString());

                    var wait = 100 - sp.ElapsedMilliseconds;
                    if (wait > 0)
                    {
                        // I expect this to be _rare_, but we need to wait to get a correct measure of the cpu
                        Thread.Sleep((int)wait);
                    }

                    var threadStats = threadsUsage.Calculate();
                    result["Threads"] = JArray.FromObject(threadStats.List);

                    using (var writer = new StreamWriter(stackTraceStream))
                    {
                        result.WriteTo(new JsonTextWriter(writer) { Indentation = 4 });
                        writer.Flush();
                    }
                }
                catch (Exception e)
                {
                    var jsonSerializer = DocumentConventions.Default.CreateSerializer();
                    jsonSerializer.Formatting = Formatting.Indented;

                    using (var errorSw = new StreamWriter(stackTraceStream))
                    {
                        jsonSerializer.Serialize(errorSw, new
                        {
                            Error = e.Message
                        });
                    }

                }
            }
        }

        private async Task<Stream> GetDebugInfoFromNodeAsync(
            JsonOperationContext context,
            RequestExecutor requestExecutor,
            IEnumerable<string> databaseNames,
            bool stackTraces)
        {
            var bodyJson = new DynamicJsonValue
            {
                [nameof(NodeDebugInfoRequestHeader.FromUrl)] = ServerStore.GetNodeHttpServerUrl(),
                [nameof(NodeDebugInfoRequestHeader.DatabaseNames)] = databaseNames
            };

            using (var ms = new MemoryStream())
            using (var writer = new BlittableJsonTextWriter(context, ms))
            {
                context.Write(writer, bodyJson);
                writer.Flush();
                ms.Flush();

                var rawStreamCommand = new GetRawStreamResultCommand($"admin/debug/remote-cluster-info-package?stacktraces={stackTraces}", ms);

                await requestExecutor.ExecuteAsync(rawStreamCommand, context);
                rawStreamCommand.Result.Position = 0;
                return rawStreamCommand.Result;
            }
        }

        private async Task WriteServerWide(ZipArchive archive, JsonOperationContext context, LocalEndpointClient localEndpointClient, bool stacktraces, string prefix = "server-wide")
        {
            //theoretically this could be parallelized,
            //however ZipArchive allows only one archive entry to be open concurrently
            foreach (var route in DebugInfoPackageUtils.Routes.Where(x => x.TypeOfRoute == RouteInformation.RouteType.None))
            {
                var entryRoute = DebugInfoPackageUtils.GetOutputPathFromRouteInformation(route, prefix);
                try
                {
                    var entry = archive.CreateEntry(entryRoute);
                    entry.ExternalAttributes = ((int)(FilePermissions.S_IRUSR | FilePermissions.S_IWUSR)) << 16;

                    using (var entryStream = entry.Open())
                    using (var writer = new BlittableJsonTextWriter(context, entryStream))
                    using (var endpointOutput = await localEndpointClient.InvokeAndReadObjectAsync(route, context))
                    {
                        context.Write(writer, endpointOutput);
                        writer.Flush();
                        await entryStream.FlushAsync();
                    }
                }
                catch (Exception e)
                {
                    DebugInfoPackageUtils.WriteExceptionAsZipEntry(e, archive, entryRoute);
                }
            }

            if (stacktraces)
                DumpStackTraces(archive, prefix);
        }

        private async Task WriteForAllLocalDatabases(ZipArchive archive, JsonOperationContext jsonOperationContext, LocalEndpointClient localEndpointClient, string prefix = null)
        {
            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext transactionOperationContext))
            using (transactionOperationContext.OpenReadTransaction())
            {
                foreach (var databaseName in ServerStore.Cluster.GetDatabaseNames(transactionOperationContext))
                {
                    using (var rawRecord = ServerStore.Cluster.ReadRawDatabaseRecord(transactionOperationContext, databaseName))
                    {
                        if (rawRecord == null ||
                            rawRecord.GetTopology().RelevantFor(ServerStore.NodeTag) == false ||
                            rawRecord.IsDisabled() ||
                            rawRecord.GetDatabaseStateStatus() == DatabaseStateStatus.RestoreInProgress ||
                            IsDatabaseBeingDeleted(ServerStore.NodeTag, rawRecord))
                            continue;
                    }

                    var path = !string.IsNullOrWhiteSpace(prefix) ? Path.Combine(prefix, databaseName) : databaseName;
                    await WriteForDatabase(archive, jsonOperationContext, localEndpointClient, databaseName, path);
                }
            }
        }

        private static bool IsDatabaseBeingDeleted(string tag, RawDatabaseRecord databaseRecord)
        {
            if (databaseRecord == null)
                return false;

            var deletionInProgress = databaseRecord.GetDeletionInProgressStatus();

            return deletionInProgress != null && deletionInProgress.TryGetValue(tag, out var delInProgress) && delInProgress != DeletionInProgressStatus.No;
        }

        private static async Task WriteForDatabase(ZipArchive archive, JsonOperationContext jsonOperationContext, LocalEndpointClient localEndpointClient, string databaseName, string path = null)
        {
            var endpointParameters = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            {
                {"database", new Microsoft.Extensions.Primitives.StringValues(databaseName)}
            };

            foreach (var route in DebugInfoPackageUtils.Routes.Where(x => x.TypeOfRoute == RouteInformation.RouteType.Databases))
            {
                try
                {
                    var entry = archive.CreateEntry(DebugInfoPackageUtils.GetOutputPathFromRouteInformation(route, path ?? databaseName));
                    entry.ExternalAttributes = ((int)(FilePermissions.S_IRUSR | FilePermissions.S_IWUSR)) << 16;

                    using (var entryStream = entry.Open())
                    using (var writer = new BlittableJsonTextWriter(jsonOperationContext, entryStream))
                    {
                        using (var endpointOutput = await localEndpointClient.InvokeAndReadObjectAsync(route, jsonOperationContext, endpointParameters))
                        {
                            jsonOperationContext.Write(writer, endpointOutput);
                            writer.Flush();
                            await entryStream.FlushAsync();
                        }
                    }
                }
                catch (Exception e)
                {
                    DebugInfoPackageUtils.WriteExceptionAsZipEntry(e, archive, path ?? databaseName);
                }
            }
        }

        private Dictionary<string, (HashSet<string>, string)> CreateUrlToDatabaseNamesMapping(TransactionOperationContext transactionOperationContext, IEnumerable<string> databaseNames)
        {
            var nodeUrlToDatabaseNames = new Dictionary<string, (HashSet<string>, string)>();
            var clusterTopology = ServerStore.GetClusterTopology(transactionOperationContext);
            foreach (var databaseName in databaseNames)
            {
                var topology = ServerStore.Cluster.ReadDatabaseTopology(transactionOperationContext, databaseName);
                var nodeUrlsAndTags = topology.AllNodes.Select(tag => (clusterTopology.GetUrlFromTag(tag), tag));
                foreach (var urlAndTag in nodeUrlsAndTags)
                {
                    if (nodeUrlToDatabaseNames.TryGetValue(urlAndTag.Item1, out (HashSet<string>, string) databaseNamesWithNodeTag))
                    {
                        databaseNamesWithNodeTag.Item1.Add(databaseName);
                    }
                    else
                    {
                        nodeUrlToDatabaseNames.Add(urlAndTag.Item1, (new HashSet<string> { databaseName }, urlAndTag.Item2));
                    }
                }
            }

            return nodeUrlToDatabaseNames;
        }

        private bool StackTraces()
        {
            if (PlatformDetails.RunningOnPosix)
                return false;

            return GetBoolValueQueryString("stacktraces", required: false) ?? false;
        }

        internal class NodeDebugInfoRequestHeader
        {
            public string FromUrl { get; set; }

            public List<string> DatabaseNames { get; set; }
        }
    }
}
