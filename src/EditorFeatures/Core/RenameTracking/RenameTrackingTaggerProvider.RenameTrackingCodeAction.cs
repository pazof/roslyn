﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Editor.Shared.Options;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Notification;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.RenameTracking;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Text.Operations;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.Implementation.RenameTracking
{
    internal sealed partial class RenameTrackingTaggerProvider
    {
        private class RenameTrackingCodeAction : CodeAction
        {
            private readonly string _title;
            private readonly IThreadingContext _threadingContext;
            private readonly Document _document;
            private readonly IEnumerable<IRefactorNotifyService> _refactorNotifyServices;
            private readonly ITextUndoHistoryRegistry _undoHistoryRegistry;
            private readonly IGlobalOptionService _globalOptions;
            private RenameTrackingCommitter _renameTrackingCommitter;

            public RenameTrackingCodeAction(
                IThreadingContext threadingContext,
                Document document,
                string title,
                IEnumerable<IRefactorNotifyService> refactorNotifyServices,
                ITextUndoHistoryRegistry undoHistoryRegistry,
                IGlobalOptionService globalOptions)
            {
                _threadingContext = threadingContext;
                _document = document;
                _title = title;
                _refactorNotifyServices = refactorNotifyServices;
                _undoHistoryRegistry = undoHistoryRegistry;
                _globalOptions = globalOptions;
            }

            public override string Title => _title;
            internal override CodeActionPriority Priority => CodeActionPriority.High;

            protected override Task<IEnumerable<CodeActionOperation>> ComputeOperationsAsync(CancellationToken cancellationToken)
            {
                // Invoked directly without previewing.
                if (_renameTrackingCommitter == null)
                {
                    if (!TryInitializeRenameTrackingCommitter(cancellationToken))
                    {
                        return SpecializedTasks.EmptyEnumerable<CodeActionOperation>();
                    }
                }

                var committerOperation = new RenameTrackingCommitterOperation(_renameTrackingCommitter, _threadingContext);
                return Task.FromResult(SpecializedCollections.SingletonEnumerable(committerOperation as CodeActionOperation));
            }

            protected override async Task<IEnumerable<CodeActionOperation>> ComputePreviewOperationsAsync(CancellationToken cancellationToken)
            {
                if (!_globalOptions.GetOption(RenameTrackingOptionsStorage.RenameTrackingPreview, _document.Project.Language) ||
                    !TryInitializeRenameTrackingCommitter(cancellationToken))
                {
                    return await SpecializedTasks.EmptyEnumerable<CodeActionOperation>().ConfigureAwait(false);
                }

                var solutionSet = await _renameTrackingCommitter.RenameSymbolAsync(cancellationToken).ConfigureAwait(false);

                return SpecializedCollections.SingletonEnumerable(
                    (CodeActionOperation)new ApplyChangesOperation(solutionSet.RenamedSolution));
            }

            private bool TryInitializeRenameTrackingCommitter(CancellationToken cancellationToken)
            {
                if (_document.TryGetText(out var text))
                {
                    var textBuffer = text.Container.GetTextBuffer();
                    if (textBuffer.Properties.TryGetProperty(typeof(StateMachine), out StateMachine stateMachine))
                    {
                        if (!stateMachine.CanInvokeRename(out _, cancellationToken: cancellationToken))
                        {
                            // The rename tracking could be dismissed while a codefix is still cached
                            // in the lightbulb. If this happens, do not perform the rename requested
                            // and instead let the user know their fix will not be applied. 
                            _document.Project.Solution.Services.GetService<INotificationService>()
                                ?.SendNotification(EditorFeaturesResources.The_rename_tracking_session_was_cancelled_and_is_no_longer_available, severity: NotificationSeverity.Error);
                            return false;
                        }

                        var snapshotSpan = stateMachine.TrackingSession.TrackingSpan.GetSpan(stateMachine.Buffer.CurrentSnapshot);
                        var newName = snapshotSpan.GetText();
                        var displayText = string.Format(EditorFeaturesResources.Rename_0_to_1, stateMachine.TrackingSession.OriginalName, newName);
                        _renameTrackingCommitter = new RenameTrackingCommitter(stateMachine, snapshotSpan, _refactorNotifyServices, _undoHistoryRegistry, displayText);
                        return true;
                    }
                }

                return false;
            }

            private sealed class RenameTrackingCommitterOperation : CodeActionOperation
            {
                private readonly RenameTrackingCommitter _committer;
                private readonly IThreadingContext _threadingContext;

                public RenameTrackingCommitterOperation(RenameTrackingCommitter committer, IThreadingContext threadingContext)
                {
                    _committer = committer;
                    _threadingContext = threadingContext;
                }

                internal override async Task<bool> TryApplyAsync(
                    Workspace workspace, Solution originalSolution, IProgressTracker progressTracker, CancellationToken cancellationToken)
                {
                    var error = await _committer.TryCommitAsync(cancellationToken).ConfigureAwait(false);
                    if (error == null)
                        return true;

                    await _threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                    var notificationService = workspace.Services.GetService<INotificationService>();
                    notificationService.SendNotification(
                        error.Value.message, EditorFeaturesResources.Rename_Symbol, error.Value.severity);
                    return false;
                }
            }
        }
    }
}
