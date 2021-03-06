﻿using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FastTests;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Backups;
using Raven.Client.Documents.Smuggler;
using Raven.Client.Exceptions;
using Raven.Server.Documents.PeriodicBackup;
using Raven.Server.Documents.PeriodicBackup.Aws;
using Raven.Tests.Core.Utils.Entities;
using Xunit;

namespace SlowTests.Server.Documents.PeriodicBackup
{
    public class Retention : RavenTestBase
    {
        private static readonly SemaphoreSlim Locker = new SemaphoreSlim(1, 1);

        [Theory]
        [InlineData(7, 3, false)]
        [InlineData(7, 3, true)]
        public async Task can_delete_backups_by_date(int backupAgeInSeconds, int numberOfBackupsToCreate, bool checkIncremental)
        {
            await Locker.WaitAsync();

            try
            {
                BackupConfigurationHelper.SkipMinimumBackupAgeToKeepValidation = true;

                var backupPath = NewDataPath(suffix: "BackupFolder");
                await CanDeleteBackupsByDate(backupAgeInSeconds, numberOfBackupsToCreate,
                    (configuration, _) =>
                    {
                        configuration.LocalSettings = new LocalSettings
                        {
                            FolderPath = backupPath
                        };
                    },
                    _ =>
                    {
                        var directories = Directory.GetDirectories(backupPath)
                            .Where(x => Directory.GetFiles(x).Any(BackupUtils.IsFullBackupOrSnapshot));

                        return Task.FromResult(directories.Count());
                    }, timeout: 15000, checkIncremental);
            }
            finally
            {
                BackupConfigurationHelper.SkipMinimumBackupAgeToKeepValidation = false;
                Locker.Release();
            }
        }

        [Theory(Skip = "Requires Amazon AWS Credentials")]
        [InlineData(7, 3, false)]
        [InlineData(7, 3, true)]
        public async Task can_delete_backups_by_date_s3(int backupAgeInSeconds, int numberOfBackupsToCreate, bool checkIncremental)
        {
            await Locker.WaitAsync();

            try
            {
                BackupConfigurationHelper.SkipMinimumBackupAgeToKeepValidation = true;

                await CanDeleteBackupsByDate(backupAgeInSeconds, numberOfBackupsToCreate,
                    (configuration, databaseName) =>
                    {
                        configuration.S3Settings = GetS3Settings(databaseName);
                    },
                    async databaseName =>
                    {
                        using (var client = new RavenAwsS3Client(GetS3Settings(databaseName)))
                        {
                            var folders = await client.ListObjectsAsync($"{client.RemoteFolderName}/", "/", listFolders: true);
                            return folders.FileInfoDetails.Count;
                        }
                    }, timeout: 120000, checkIncremental);
            }
            finally
            {
                BackupConfigurationHelper.SkipMinimumBackupAgeToKeepValidation = false;
                Locker.Release();
            }
        }

        [Fact]
        public async Task configuration_validation()
        {
            await Locker.WaitAsync();

            try
            {
                Assert.False(BackupConfigurationHelper.SkipMinimumBackupAgeToKeepValidation);

                using (var store = GetDocumentStore())
                {
                    var config = new PeriodicBackupConfiguration
                    {
                        IncrementalBackupFrequency = "30 3 L * ?",
                        RetentionPolicy = new RetentionPolicy
                        {
                            MinimumBackupAgeToKeep = TimeSpan.FromDays(-5)
                        }
                    };

                    var error = await Assert.ThrowsAsync<RavenException>(() => store.Maintenance.SendAsync(new UpdatePeriodicBackupOperation(config)));
                    Assert.True(error.Message.Contains($"{nameof(RetentionPolicy.MinimumBackupAgeToKeep)} must be positive"));

                    config.RetentionPolicy.MinimumBackupAgeToKeep = TimeSpan.FromHours(12);
                    error = await Assert.ThrowsAsync<RavenException>(() => store.Maintenance.SendAsync(new UpdatePeriodicBackupOperation(config)));
                    Assert.True(error.Message.Contains($"{nameof(RetentionPolicy.MinimumBackupAgeToKeep)} must be bigger than one day"));
                }
            }
            finally
            {
                Locker.Release();
            }
        }

        private async Task CanDeleteBackupsByDate(
            int backupAgeInSeconds,
            int numberOfBackupsToCreate,
            Action<PeriodicBackupConfiguration, string> modifyConfiguration,
            Func<string, Task<int>> getDirectoriesCount,
            int timeout, bool checkIncremental = false)
        {
            var minimumBackupAgeToKeep = TimeSpan.FromSeconds(backupAgeInSeconds);

            using (var store = GetDocumentStore())
            {
                var config = new PeriodicBackupConfiguration
                {
                    IncrementalBackupFrequency = "30 3 L * ?",
                    RetentionPolicy = new RetentionPolicy
                    {
                        MinimumBackupAgeToKeep = minimumBackupAgeToKeep
                    }
                };

                modifyConfiguration(config, store.Database);

                var backupTaskId = (await store.Maintenance.SendAsync(new UpdatePeriodicBackupOperation(config))).TaskId;

                var lastEtag = 0L;
                var userId = "";
                for (var i = 0; i < numberOfBackupsToCreate; i++)
                {

                    using (var session = store.OpenAsyncSession())
                    {
                        var user = new User { Name = "Grisha" };
                        await session.StoreAsync(user);
                        userId = user.Id;
                        await session.SaveChangesAsync();
                    }

                    // create full backup
                    lastEtag = await CreateBackup(store, true, backupTaskId, lastEtag, timeout);

                    using (var session = store.OpenAsyncSession())
                    {
                        var user = await session.LoadAsync<User>(userId);
                        user.Age = 33;
                        await session.SaveChangesAsync();
                    }

                    // create incremental backup
                    lastEtag = await CreateBackup(store, false, backupTaskId, lastEtag, timeout);
                }

                await Task.Delay(minimumBackupAgeToKeep + TimeSpan.FromSeconds(5));

                if (checkIncremental)
                {
                    using (var session = store.OpenAsyncSession())
                    {
                        var user = await session.LoadAsync<User>(userId);
                        user.Name = "Egor";
                        user.Age = 322;
                        await session.SaveChangesAsync();
                    }

                    // create incremental backup with retention policy
                    lastEtag = await CreateBackup(store, false, backupTaskId, lastEtag, timeout);
                }

                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Grisha" });
                    await session.SaveChangesAsync();
                }
                await CreateBackup(store, true, backupTaskId, lastEtag, timeout);

                var directoriesCount = await getDirectoriesCount(store.Database);
                var expectedNumberOfDirectories = checkIncremental ? 2 : 1;
                Assert.Equal(expectedNumberOfDirectories, directoriesCount);
            }
        }

        private async Task<long> CreateBackup(DocumentStore store, bool isFullBackup, long backupTaskId, long lastEtag, int timeout)
        {
            await store.Maintenance.SendAsync(new StartBackupOperation(isFullBackup, backupTaskId));
            var operation = new GetPeriodicBackupStatusOperation(backupTaskId);
            var value = WaitForValue(() =>
            {
                var status = store.Maintenance.Send(operation).Status;
                if (status == null)
                    return false;

                if (status.LastEtag <= lastEtag)
                    return false;

                lastEtag = status.LastEtag.Value;
                return true;
            }, true, timeout: timeout);
            Assert.True(value);

            return lastEtag;
        }

        private static S3Settings GetS3Settings(string databaseName, [CallerMemberName] string remoteFolderName = null)
        {
            return new S3Settings
            {
                AwsAccessKey = null,
                AwsSecretKey = null,
                AwsRegionName = null,
                BucketName = "ravendb-test",
                RemoteFolderName = $"{remoteFolderName}/{databaseName}"
            };
        }
    }
}
