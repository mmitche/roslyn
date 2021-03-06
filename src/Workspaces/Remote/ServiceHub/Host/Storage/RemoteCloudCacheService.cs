﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.CodeAnalysis.PersistentStorage;
using Microsoft.VisualStudio.LanguageServices.Storage;
using Microsoft.VisualStudio.RpcContracts.Caching;

namespace Microsoft.CodeAnalysis.Remote.Storage
{
    internal class RemoteCloudCachePersistentStorage : AbstractCloudCachePersistentStorage
    {
        public RemoteCloudCachePersistentStorage(
            ICacheService cacheService,
            SolutionKey solutionKey,
            string workingFolderPath,
            string relativePathBase,
            string databaseFilePath)
            : base(cacheService, solutionKey, workingFolderPath, relativePathBase, databaseFilePath)
        {
        }

        public override void Dispose()
        {
            if (this.CacheService is IAsyncDisposable asyncDisposable)
            {
                asyncDisposable.DisposeAsync().AsTask().Wait();
            }
            else if (this.CacheService is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
