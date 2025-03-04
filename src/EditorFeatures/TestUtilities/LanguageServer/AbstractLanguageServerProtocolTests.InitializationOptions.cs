﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.Options;
using LSP = Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Roslyn.Test.Utilities
{
    public abstract partial class AbstractLanguageServerProtocolTests
    {
        internal record struct InitializationOptions()
        {
            internal string[] SourceGeneratedMarkups { get; init; } = Array.Empty<string>();
            internal LSP.ClientCapabilities ClientCapabilities { get; init; } = new LSP.ClientCapabilities();
            internal WellKnownLspServerKinds ServerKind { get; init; } = WellKnownLspServerKinds.AlwaysActiveVSLspServer;
            internal Action<IGlobalOptionService>? OptionUpdater { get; init; } = null;
        }
    }
}
