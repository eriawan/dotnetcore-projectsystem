﻿// Licensed to the .NET Foundation under one or more agreements. The .NET Foundation licenses this file to you under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Debugger.Contracts.HotReload;
using Microsoft.VisualStudio.HotReload.Components.DeltaApplier;

namespace Microsoft.VisualStudio.ProjectSystem.VS.HotReload
{
    internal class ProjectHotReloadSession : IManagedHotReloadAgent, IProjectHotReloadSession
    {
        private readonly string _runtimeVersion;
        private readonly Lazy<IHotReloadAgentManagerClient> _hotReloadAgentManagerClient;
        private readonly Lazy<IHotReloadDiagnosticOutputService> _hotReloadOutputService;
        private readonly Lazy<IManagedDeltaApplierCreator> _deltaApplierCreator;
        private readonly IProjectHotReloadSessionCallback _callback;

        private bool _sessionActive;
        private IDeltaApplier? _deltaApplier;

        public ProjectHotReloadSession(
            string name,
            string runtimeVersion,
            Lazy<IHotReloadAgentManagerClient> hotReloadAgentManagerClient,
            Lazy<IHotReloadDiagnosticOutputService> hotReloadOutputService,
            Lazy<IManagedDeltaApplierCreator> deltaApplierCreator,
            IProjectHotReloadSessionCallback callback)
        {
            Name = name;
            _runtimeVersion = runtimeVersion;
            _hotReloadAgentManagerClient = hotReloadAgentManagerClient;
            _hotReloadOutputService = hotReloadOutputService;
            _deltaApplierCreator = deltaApplierCreator;
            _callback = callback;
        }

        // IProjectHotReloadSession

        public string Name { get; }

        public async Task ApplyChangesAsync(CancellationToken cancellationToken)
        {
            if (_sessionActive)
            {
                await _hotReloadAgentManagerClient.Value.ApplyUpdatesAsync(cancellationToken);
            }
        }

        public async Task<bool> ApplyLaunchVariablesAsync(IDictionary<string, string> envVars, CancellationToken cancellationToken)
        {
            EnsureDeltaApplierforSession();
            if (_deltaApplier is not null)
            {
                // TODO: Simplify this once ApplyProcessEnvironmentVariablesAsync takes an IDictionary instead of a Dictionary.
                if (envVars is Dictionary<string, string> envVarsAsDictionary)
                {
                    return await _deltaApplier.ApplyProcessEnvironmentVariablesAsync(envVarsAsDictionary, cancellationToken);
                }
                else
                {
                    envVarsAsDictionary = new Dictionary<string, string>(envVars);
                    bool result = await _deltaApplier.ApplyProcessEnvironmentVariablesAsync(envVarsAsDictionary, cancellationToken);
                    foreach ((string name, string value) in envVarsAsDictionary)
                    {
                        envVars[name] = value;
                    }

                    return result;
                }
            }

            return false;
        }

        // TODO: remove when Web Tools is no longer calling this method.
        public Task StartSessionAsync(CancellationToken cancellationToken)
        {
            return StartSessionAsync(runningUnderDebugger: false, cancellationToken);
        }

        public async Task StartSessionAsync(bool runningUnderDebugger, CancellationToken cancellationToken)
        {
            if (_sessionActive)
            {
                throw new InvalidOperationException("Attempting to start a Hot Reload session that is already running.");
            }

            HotReloadAgentFlags flags = runningUnderDebugger ? HotReloadAgentFlags.IsDebuggedProcess : HotReloadAgentFlags.None;
            await _hotReloadAgentManagerClient.Value.AgentStartedAsync(this, flags, cancellationToken);
            WriteToOutputWindow(VSResources.HotReloadStartSession);
            _sessionActive = true;
            EnsureDeltaApplierforSession();
        }

        public async Task StopSessionAsync(CancellationToken cancellationToken)
        {
            if (_sessionActive)
            {
                _sessionActive = false;

                await _hotReloadAgentManagerClient.Value.AgentTerminatedAsync(this, cancellationToken);
                WriteToOutputWindow(VSResources.HotReloadStopSession);
            }
        }

        // IManagedHotReloadAgent

        public async ValueTask ApplyUpdatesAsync(ImmutableArray<ManagedHotReloadUpdate> updates, CancellationToken cancellationToken)
        {
            if (!_sessionActive)
            {
                WriteToOutputWindow($"ApplyUpdatesAsync called but the session is not active.");
                return;
            }

            if (_deltaApplier is null)
            {
                WriteToOutputWindow($"ApplyUpdatesAsync called but we have no delta applier.");
            }

            if (!_sessionActive || _deltaApplier is null)
            {
                return;
            }

            try
            {
                WriteToOutputWindow(VSResources.HotReloadSendingUpdates);

                ApplyResult result = await _deltaApplier.ApplyUpdatesAsync(updates, cancellationToken);
                if (result == ApplyResult.Success || result == ApplyResult.SuccessRefreshUI)
                {
                    WriteToOutputWindow(VSResources.HotReloadApplyUpdatesSuccessful);
                    if (_callback is not null)
                    {
                        await _callback.OnAfterChangesAppliedAsync(cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                string message = $"{ex.GetType()}: {ex.Message}";

                WriteToOutputWindow(string.Format(VSResources.HotReloadApplyUpdatesFailure, message));
                throw;
            }
        }

        public async ValueTask<ImmutableArray<string>> GetCapabilitiesAsync(CancellationToken cancellationToken)
        {
            // Delegate to the delta applier for the session
            if (_deltaApplier is not null)
            {
                return await _deltaApplier.GetCapabilitiesAsync(cancellationToken);
            }
            return ImmutableArray<string>.Empty;
        }

        public ValueTask ReportDiagnosticsAsync(ImmutableArray<ManagedHotReloadDiagnostic> diagnostics, CancellationToken cancellationToken)
        {
            WriteToOutputWindow(VSResources.HotReloadErrorsInApplication);

            foreach (ManagedHotReloadDiagnostic diagnostic in diagnostics)
            {
                WriteToOutputWindow($"{diagnostic.FilePath}({diagnostic.Span.StartLine},{diagnostic.Span.StartColumn},{diagnostic.Span.EndLine},{diagnostic.Span.EndColumn}): {diagnostic.Message}");
            }

            return new ValueTask(Task.CompletedTask);
        }

        public async ValueTask RestartAsync(CancellationToken cancellationToken)
        {
            WriteToOutputWindow(VSResources.HotReloadRestartInProgress);

            await _callback.RestartProjectAsync(cancellationToken);

            // TODO: Should we stop the session here? Or does someone else do it?
            // TODO: Should we handle rebuilding here? Or do we expect the callback to handle it?
        }

        public async ValueTask StopAsync(CancellationToken cancellationToken)
        {
            WriteToOutputWindow(VSResources.HotReloadStoppingApplication);

            await _callback.StopProjectAsync(cancellationToken);

            // TODO: Should we stop the session here? Or does someone else do it?
        }

        public ValueTask<bool> SupportsRestartAsync(CancellationToken cancellationToken)
        {
            return new ValueTask<bool>(_callback.SupportsRestart);
        }

        private void WriteToOutputWindow(string message)
        {
            _hotReloadOutputService.Value.WriteLine($"{Name}: {message}");
        }

        private void EnsureDeltaApplierforSession()
        {
            if (_deltaApplier is null)
            {
                _deltaApplier = (IDeltaApplier)(_callback.GetDeltaApplier()
                    ?? _deltaApplierCreator.Value.CreateManagedDeltaApplier(_runtimeVersion));
            }
        }
    }
}
