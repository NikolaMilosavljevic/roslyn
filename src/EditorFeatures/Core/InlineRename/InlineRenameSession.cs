﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Editor.BackgroundWorkIndicator;
using Microsoft.CodeAnalysis.Editor.Host;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Editor.Undo;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.InlineRename;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.Notification;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.Implementation.InlineRename
{
    internal partial class InlineRenameSession : IInlineRenameSession, IFeatureController
    {
        private readonly Workspace _workspace;
        private readonly IUIThreadOperationExecutor _uiThreadOperationExecutor;
        private readonly ITextBufferAssociatedViewService _textBufferAssociatedViewService;
        private readonly ITextBufferFactoryService _textBufferFactoryService;
        private readonly IFeatureService _featureService;
        private readonly IFeatureDisableToken _completionDisabledToken;
        private readonly IEnumerable<IRefactorNotifyService> _refactorNotifyServices;
        private readonly IAsynchronousOperationListener _asyncListener;
        private readonly Solution _baseSolution;
        private readonly Document _triggerDocument;
        private readonly ITextView _triggerView;
        private readonly IDisposable _inlineRenameSessionDurationLogBlock;
        private readonly IThreadingContext _threadingContext;
        public readonly InlineRenameService RenameService;

        private bool _dismissed;
        private bool _isApplyingEdit;
        private string _replacementText;
        private SymbolRenameOptions _options;
        private bool _previewChanges;
        private readonly Dictionary<ITextBuffer, OpenTextBufferManager> _openTextBuffers = new Dictionary<ITextBuffer, OpenTextBufferManager>();

        /// <summary>
        /// The original <see cref="SnapshotSpan"/> for the identifier that rename was triggered on
        /// </summary>
        public SnapshotSpan TriggerSpan { get; }

        /// <summary>
        /// If non-null, the current text of the replacement. Linked spans added will automatically be updated with this
        /// text.
        /// </summary>
        public string ReplacementText
        {
            get
            {
                return _replacementText;
            }
            private set
            {
                _replacementText = value;
                ReplacementTextChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Information about whether a file rename should be allowed as part
        /// of the rename operation, as determined by the language
        /// </summary>
        public InlineRenameFileRenameInfo FileRenameInfo { get; }

        /// <summary>
        /// Rename session held alive with the OOP server.  This allows us to pin the initial solution snapshot over on
        /// the oop side, which is valuable for preventing it from constantly being dropped/synced on every conflict
        /// resolution step.
        /// </summary>
        private readonly IRemoteRenameKeepAliveSession _keepAliveSession;

        /// <summary>
        /// The task which computes the main rename locations against the original workspace
        /// snapshot.
        /// </summary>
        private JoinableTask<IInlineRenameLocationSet> _allRenameLocationsTask;

        /// <summary>
        /// The cancellation token for most work being done by the inline rename session. This
        /// includes the <see cref="_allRenameLocationsTask"/> tasks.
        /// </summary>
        private readonly CancellationTokenSource _cancellationTokenSource = new();

        /// <summary>
        /// This task is a continuation of the <see cref="_allRenameLocationsTask"/> that is the result of computing
        /// the resolutions of the rename spans for the current replacementText.
        /// </summary>
        private JoinableTask<IInlineRenameReplacementInfo> _conflictResolutionTask;

        /// <summary>
        /// The cancellation source for <see cref="_conflictResolutionTask"/>.
        /// </summary>
        private CancellationTokenSource _conflictResolutionTaskCancellationSource = new CancellationTokenSource();

        private readonly IInlineRenameInfo _renameInfo;

        /// <summary>
        /// The initial text being renamed.
        /// </summary>
        private readonly string _initialRenameText;

        public InlineRenameSession(
            IThreadingContext threadingContext,
            InlineRenameService renameService,
            Workspace workspace,
            SnapshotSpan triggerSpan,
            IInlineRenameInfo renameInfo,
            SymbolRenameOptions options,
            bool previewChanges,
            IUIThreadOperationExecutor uiThreadOperationExecutor,
            ITextBufferAssociatedViewService textBufferAssociatedViewService,
            ITextBufferFactoryService textBufferFactoryService,
            IFeatureServiceFactory featureServiceFactory,
            IEnumerable<IRefactorNotifyService> refactorNotifyServices,
            IAsynchronousOperationListener asyncListener)
        {
            // This should always be touching a symbol since we verified that upon invocation
            _threadingContext = threadingContext;
            _renameInfo = renameInfo;

            TriggerSpan = triggerSpan;
            _triggerDocument = triggerSpan.Snapshot.GetOpenDocumentInCurrentContextWithChanges();
            if (_triggerDocument == null)
            {
                throw new InvalidOperationException(EditorFeaturesResources.The_triggerSpan_is_not_included_in_the_given_workspace);
            }

            _inlineRenameSessionDurationLogBlock = Logger.LogBlock(FunctionId.Rename_InlineSession, CancellationToken.None);

            _workspace = workspace;
            _workspace.WorkspaceChanged += OnWorkspaceChanged;

            _textBufferFactoryService = textBufferFactoryService;
            _textBufferAssociatedViewService = textBufferAssociatedViewService;
            _textBufferAssociatedViewService.SubjectBuffersConnected += OnSubjectBuffersConnected;

            // Disable completion when an inline rename session starts
            _featureService = featureServiceFactory.GlobalFeatureService;
            _completionDisabledToken = _featureService.Disable(PredefinedEditorFeatureNames.Completion, this);
            RenameService = renameService;
            _uiThreadOperationExecutor = uiThreadOperationExecutor;
            _refactorNotifyServices = refactorNotifyServices;
            _asyncListener = asyncListener;
            _triggerView = textBufferAssociatedViewService.GetAssociatedTextViews(triggerSpan.Snapshot.TextBuffer).FirstOrDefault(v => v.HasAggregateFocus) ??
                textBufferAssociatedViewService.GetAssociatedTextViews(triggerSpan.Snapshot.TextBuffer).First();

            _options = options;
            _previewChanges = previewChanges;

            _initialRenameText = triggerSpan.GetText();
            this.ReplacementText = _initialRenameText;

            _baseSolution = _triggerDocument.Project.Solution;
            this.UndoManager = workspace.Services.GetService<IInlineRenameUndoManager>();

            if (_renameInfo is IInlineRenameInfoWithFileRename renameInfoWithFileRename)
            {
                FileRenameInfo = renameInfoWithFileRename.GetFileRenameInfo();
            }
            else
            {
                FileRenameInfo = InlineRenameFileRenameInfo.NotAllowed;
            }

            // Open a session to oop, syncing our solution to it and pinning it there.  The connection will close once
            // _cancellationTokenSource is canceled (which we always do when the session is finally ended).
            _keepAliveSession = Renamer.CreateRemoteKeepAliveSession(_baseSolution, asyncListener);
            InitializeOpenBuffers(triggerSpan);
        }

        public string OriginalSymbolName => _renameInfo.DisplayName;

        // Used to aid the investigation of https://github.com/dotnet/roslyn/issues/7364
        private class NullTextBufferException : Exception
        {
#pragma warning disable IDE0052 // Remove unread private members
            private readonly Document _document;
            private readonly SourceText _text;
#pragma warning restore IDE0052 // Remove unread private members

            public NullTextBufferException(Document document, SourceText text)
                : base("Cannot retrieve textbuffer from document.")
            {
                _document = document;
                _text = text;
            }
        }

        private void InitializeOpenBuffers(SnapshotSpan triggerSpan)
        {
            using (Logger.LogBlock(FunctionId.Rename_CreateOpenTextBufferManagerForAllOpenDocs, CancellationToken.None))
            {
                var openBuffers = new HashSet<ITextBuffer>();
                foreach (var d in _workspace.GetOpenDocumentIds())
                {
                    var document = _baseSolution.GetDocument(d);
                    if (document == null)
                    {
                        continue;
                    }

                    Contract.ThrowIfFalse(document.TryGetText(out var text));
                    Contract.ThrowIfNull(text);

                    var textSnapshot = text.FindCorrespondingEditorTextSnapshot();
                    if (textSnapshot == null)
                    {
                        FatalError.ReportAndCatch(new NullTextBufferException(document, text));
                        continue;
                    }

                    Contract.ThrowIfNull(textSnapshot.TextBuffer);

                    openBuffers.Add(textSnapshot.TextBuffer);
                }

                foreach (var buffer in openBuffers)
                {
                    TryPopulateOpenTextBufferManagerForBuffer(buffer);
                }
            }

            var startingSpan = triggerSpan.Span;

            // Select this span if we didn't already have something selected
            var selections = _triggerView.Selection.GetSnapshotSpansOnBuffer(triggerSpan.Snapshot.TextBuffer);
            if (!selections.Any() ||
                selections.First().IsEmpty ||
                !startingSpan.Contains(selections.First()))
            {
                _triggerView.SetSelection(new SnapshotSpan(triggerSpan.Snapshot, startingSpan));
            }

            this.UndoManager.CreateInitialState(this.ReplacementText, _triggerView.Selection, new SnapshotSpan(triggerSpan.Snapshot, startingSpan));
            _openTextBuffers[triggerSpan.Snapshot.TextBuffer].SetReferenceSpans(SpecializedCollections.SingletonEnumerable(startingSpan.ToTextSpan()));

            UpdateReferenceLocationsTask();

            RenameTrackingDismisser.DismissRenameTracking(_workspace, _workspace.GetOpenDocumentIds());
        }

        private bool TryPopulateOpenTextBufferManagerForBuffer(ITextBuffer buffer)
        {
            _threadingContext.ThrowIfNotOnUIThread();
            VerifyNotDismissed();

            if (_workspace.Kind == WorkspaceKind.Interactive)
            {
                Debug.Assert(buffer.GetRelatedDocuments().Count() == 1);
                Debug.Assert(buffer.IsReadOnly(0) == buffer.IsReadOnly(VisualStudio.Text.Span.FromBounds(0, buffer.CurrentSnapshot.Length))); // All or nothing.
                if (buffer.IsReadOnly(0))
                {
                    return false;
                }
            }

            if (!_openTextBuffers.ContainsKey(buffer) && buffer.SupportsRename())
            {
                _openTextBuffers[buffer] = new OpenTextBufferManager(this, buffer, _workspace, _textBufferFactoryService);
                return true;
            }

            return _openTextBuffers.ContainsKey(buffer);
        }

        private void OnSubjectBuffersConnected(object sender, SubjectBuffersConnectedEventArgs e)
        {
            _threadingContext.ThrowIfNotOnUIThread();
            foreach (var buffer in e.SubjectBuffers)
            {
                if (buffer.GetWorkspace() == _workspace)
                {
                    if (TryPopulateOpenTextBufferManagerForBuffer(buffer))
                    {
                        _openTextBuffers[buffer].ConnectToView(e.TextView);
                    }
                }
            }
        }

        private void UpdateReferenceLocationsTask()
        {
            _threadingContext.ThrowIfNotOnUIThread();

            var asyncToken = _asyncListener.BeginAsyncOperation("UpdateReferencesTask");

            var currentOptions = _options;
            var currentRenameLocationsTask = _allRenameLocationsTask;
            var cancellationToken = _cancellationTokenSource.Token;

            _allRenameLocationsTask = _threadingContext.JoinableTaskFactory.RunAsync(async () =>
            {
                // Join prior work before proceeding, since it performs a required state update.
                // https://github.com/dotnet/roslyn/pull/34254#discussion_r267024593
                if (currentRenameLocationsTask != null)
                    await _allRenameLocationsTask.JoinAsync(cancellationToken).ConfigureAwait(false);

                await TaskScheduler.Default;
                var inlineRenameLocations = await _renameInfo.FindRenameLocationsAsync(currentOptions, cancellationToken).ConfigureAwait(false);

                // It's unfortunate that _allRenameLocationsTask has a UI thread dependency (prevents continuations
                // from running prior to the completion of the UI operation), but the implementation does not currently
                // follow the originally-intended design.
                // https://github.com/dotnet/roslyn/issues/40890
                await _threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true, cancellationToken);

                RaiseSessionSpansUpdated(inlineRenameLocations.Locations.ToImmutableArray());

                return inlineRenameLocations;
            });

            _allRenameLocationsTask.Task.CompletesAsyncOperation(asyncToken);

            UpdateConflictResolutionTask();
            QueueApplyReplacements();
        }

        public Workspace Workspace => _workspace;
        public SymbolRenameOptions Options => _options;
        public bool PreviewChanges => _previewChanges;
        public bool HasRenameOverloads => _renameInfo.HasOverloads;
        public bool MustRenameOverloads => _renameInfo.MustRenameOverloads;

        public IInlineRenameUndoManager UndoManager { get; }

        public event EventHandler<ImmutableArray<InlineRenameLocation>> ReferenceLocationsChanged;
        public event EventHandler<IInlineRenameReplacementInfo> ReplacementsComputed;
        public event EventHandler ReplacementTextChanged;

        internal OpenTextBufferManager GetBufferManager(ITextBuffer buffer)
            => _openTextBuffers[buffer];

        internal bool TryGetBufferManager(ITextBuffer buffer, out OpenTextBufferManager bufferManager)
            => _openTextBuffers.TryGetValue(buffer, out bufferManager);

        public void RefreshRenameSessionWithOptionsChanged(SymbolRenameOptions newOptions)
        {
            if (_options == newOptions)
            {
                return;
            }

            _threadingContext.ThrowIfNotOnUIThread();
            VerifyNotDismissed();

            _options = newOptions;
            UpdateReferenceLocationsTask();
        }

        public void SetPreviewChanges(bool value)
        {
            _threadingContext.ThrowIfNotOnUIThread();
            VerifyNotDismissed();

            _previewChanges = value;
        }

        private void VerifyNotDismissed()
        {
            if (_dismissed)
            {
                throw new InvalidOperationException(EditorFeaturesResources.This_session_has_already_been_dismissed);
            }
        }

        private void OnWorkspaceChanged(object sender, WorkspaceChangeEventArgs args)
        {
            if (args.Kind != WorkspaceChangeKind.DocumentChanged)
            {
                if (!_dismissed)
                {
                    this.Cancel();
                }
            }
        }

        private void RaiseSessionSpansUpdated(ImmutableArray<InlineRenameLocation> locations)
        {
            _threadingContext.ThrowIfNotOnUIThread();
            SetReferenceLocations(locations);

            // It's OK to call SetReferenceLocations with all documents, including unchangeable ones,
            // because they can't be opened, so the _openTextBuffers loop won't matter. In fact, the entire
            // inline rename is oblivious to unchangeable documents, we just need to filter out references
            // in them to avoid displaying them in the UI.
            // https://github.com/dotnet/roslyn/issues/41242
            if (_workspace.IgnoreUnchangeableDocumentsWhenApplyingChanges)
            {
                locations = locations.WhereAsArray(l => l.Document.CanApplyChange());
            }

            ReferenceLocationsChanged?.Invoke(this, locations);
        }

        private void SetReferenceLocations(ImmutableArray<InlineRenameLocation> locations)
        {
            _threadingContext.ThrowIfNotOnUIThread();

            var locationsByDocument = locations.ToLookup(l => l.Document.Id);

            _isApplyingEdit = true;
            foreach (var textBuffer in _openTextBuffers.Keys)
            {
                var documents = textBuffer.AsTextContainer().GetRelatedDocuments();

                if (!documents.Any(static (d, locationsByDocument) => locationsByDocument.Contains(d.Id), locationsByDocument))
                {
                    _openTextBuffers[textBuffer].SetReferenceSpans(SpecializedCollections.EmptyEnumerable<TextSpan>());
                }
                else
                {
                    var spans = documents.SelectMany(d => locationsByDocument[d.Id]).Select(l => l.TextSpan).Distinct();
                    _openTextBuffers[textBuffer].SetReferenceSpans(spans);
                }
            }

            _isApplyingEdit = false;
        }

        /// <summary>
        /// Updates the replacement text for the rename session and propagates it to all live buffers.
        /// </summary>
        internal void ApplyReplacementText(string replacementText, bool propagateEditImmediately)
        {
            _threadingContext.ThrowIfNotOnUIThread();
            VerifyNotDismissed();
            this.ReplacementText = _renameInfo.GetFinalSymbolName(replacementText);

            var asyncToken = _asyncListener.BeginAsyncOperation(nameof(ApplyReplacementText));

            Action propagateEditAction = delegate
            {
                _threadingContext.ThrowIfNotOnUIThread();

                if (_dismissed)
                {
                    asyncToken.Dispose();
                    return;
                }

                _isApplyingEdit = true;
                using (Logger.LogBlock(FunctionId.Rename_ApplyReplacementText, replacementText, _cancellationTokenSource.Token))
                {
                    foreach (var openBuffer in _openTextBuffers.Values)
                    {
                        openBuffer.ApplyReplacementText();
                    }
                }

                _isApplyingEdit = false;

                // We already kicked off UpdateConflictResolutionTask below (outside the delegate).
                // Now that we are certain the replacement text has been propagated to all of the
                // open buffers, it is safe to actually apply the replacements it has calculated.
                // See https://devdiv.visualstudio.com/DevDiv/_workitems?_a=edit&id=227513
                QueueApplyReplacements();

                asyncToken.Dispose();
            };

            // Start the conflict resolution task but do not apply the results immediately. The
            // buffer changes performed in propagateEditAction can cause source control modal
            // dialogs to show. Those dialogs pump, and yield the UI thread to whatever work is
            // waiting to be done there, including our ApplyReplacements work. If ApplyReplacements
            // starts running on the UI thread while propagateEditAction is still updating buffers
            // on the UI thread, we crash because we try to enumerate the undo stack while an undo
            // transaction is still in process. Therefore, we defer QueueApplyReplacements until
            // after the buffers have been edited, and any modal dialogs have been completed.
            // In addition to avoiding the crash, this also ensures that the resolved conflict text
            // is applied after the simple text change is propagated.
            // See https://devdiv.visualstudio.com/DevDiv/_workitems?_a=edit&id=227513
            UpdateConflictResolutionTask();

            if (propagateEditImmediately)
            {
                propagateEditAction();
            }
            else
            {
                // When responding to a text edit, we delay propagating the edit until the first transaction completes.
                _threadingContext.JoinableTaskFactory.RunAsync(async () =>
                {
                    await _threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
                    propagateEditAction();
                });
            }
        }

        private void UpdateConflictResolutionTask()
        {
            _threadingContext.ThrowIfNotOnUIThread();

            _conflictResolutionTaskCancellationSource.Cancel();
            _conflictResolutionTaskCancellationSource = new CancellationTokenSource();

            // If the replacement text is empty, we do not update the results of the conflict
            // resolution task. We instead wait for a non-empty identifier.
            if (this.ReplacementText == string.Empty)
            {
                return;
            }

            var replacementText = this.ReplacementText;
            var options = _options;
            var cancellationToken = _conflictResolutionTaskCancellationSource.Token;

            var asyncToken = _asyncListener.BeginAsyncOperation(nameof(UpdateConflictResolutionTask));

            _conflictResolutionTask = _threadingContext.JoinableTaskFactory.RunAsync(async () =>
            {
                // Join prior work before proceeding, since it performs a required state update.
                // https://github.com/dotnet/roslyn/pull/34254#discussion_r267024593
                //
                // If cancellation of the conflict resolution task is requested before the rename locations task
                // completes, we do not need to wait for rename before cancelling. The next conflict resolution task
                // will wait on the latest rename location task if/when necessary.
                var result = await _allRenameLocationsTask.JoinAsync(cancellationToken).ConfigureAwait(false);
                await TaskScheduler.Default;

                return await result.GetReplacementsAsync(replacementText, options, cancellationToken).ConfigureAwait(false);
            });

            _conflictResolutionTask.Task.CompletesAsyncOperation(asyncToken);
        }

        [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "False positive in methods using JTF: https://github.com/dotnet/roslyn-analyzers/issues/4283")]
        private void QueueApplyReplacements()
        {
            // If the replacement text is empty, we do not update the results of the conflict
            // resolution task. We instead wait for a non-empty identifier.
            if (this.ReplacementText == string.Empty)
            {
                return;
            }

            var cancellationToken = _conflictResolutionTaskCancellationSource.Token;
            var asyncToken = _asyncListener.BeginAsyncOperation(nameof(QueueApplyReplacements));
            var replacementOperation = _threadingContext.JoinableTaskFactory.RunAsync(async () =>
            {
                var replacementInfo = await _conflictResolutionTask.JoinAsync(CancellationToken.None).ConfigureAwait(false);
                if (replacementInfo == null || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                // Switch to a background thread for expensive work
                await TaskScheduler.Default;
                var computedMergeResult = await ComputeMergeResultAsync(replacementInfo, cancellationToken);
                await _threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true, cancellationToken);
                ApplyReplacements(computedMergeResult.replacementInfo, computedMergeResult.mergeResult, cancellationToken);
            });
            replacementOperation.Task.CompletesAsyncOperation(asyncToken);
        }

        private async Task<(IInlineRenameReplacementInfo replacementInfo, LinkedFileMergeSessionResult mergeResult)> ComputeMergeResultAsync(IInlineRenameReplacementInfo replacementInfo, CancellationToken cancellationToken)
        {
            var diffMergingSession = new LinkedFileDiffMergingSession(_baseSolution, replacementInfo.NewSolution, replacementInfo.NewSolution.GetChanges(_baseSolution));
            var mergeResult = await diffMergingSession.MergeDiffsAsync(mergeConflictHandler: null, cancellationToken: cancellationToken).ConfigureAwait(false);
            return (replacementInfo, mergeResult);
        }

        private void ApplyReplacements(IInlineRenameReplacementInfo replacementInfo, LinkedFileMergeSessionResult mergeResult, CancellationToken cancellationToken)
        {
            _threadingContext.ThrowIfNotOnUIThread();
            cancellationToken.ThrowIfCancellationRequested();

            RaiseReplacementsComputed(replacementInfo);

            _isApplyingEdit = true;
            foreach (var textBuffer in _openTextBuffers.Keys)
            {
                var documents = textBuffer.CurrentSnapshot.GetRelatedDocumentsWithChanges();
                if (documents.Any())
                {
                    var textBufferManager = _openTextBuffers[textBuffer];
                    textBufferManager.ApplyConflictResolutionEdits(replacementInfo, mergeResult, documents, cancellationToken);
                }
            }

            _isApplyingEdit = false;
        }

        private void RaiseReplacementsComputed(IInlineRenameReplacementInfo resolution)
        {
            _threadingContext.ThrowIfNotOnUIThread();
            ReplacementsComputed?.Invoke(this, resolution);
        }

        private void LogRenameSession(RenameLogMessage.UserActionOutcome outcome, bool previewChanges)
        {
            if (_conflictResolutionTask == null)
            {
                return;
            }

            var conflictResolutionFinishedComputing = _conflictResolutionTask.Task.Status == TaskStatus.RanToCompletion;

            if (conflictResolutionFinishedComputing)
            {
                var result = _conflictResolutionTask.Task.Result;
                var replacementKinds = result.GetAllReplacementKinds().ToList();

                Logger.Log(FunctionId.Rename_InlineSession_Session, RenameLogMessage.Create(
                    _options,
                    outcome,
                    conflictResolutionFinishedComputing,
                    previewChanges,
                    replacementKinds));
            }
            else
            {
                Debug.Assert(outcome.HasFlag(RenameLogMessage.UserActionOutcome.Canceled));
                Logger.Log(FunctionId.Rename_InlineSession_Session, RenameLogMessage.Create(
                    _options,
                    outcome,
                    conflictResolutionFinishedComputing,
                    previewChanges,
                    SpecializedCollections.EmptyList<InlineRenameReplacementKind>()));
            }
        }

        public void Cancel()
        {
            _threadingContext.ThrowIfNotOnUIThread();
            VerifyNotDismissed();

            // This wait is safe.  We are not passing the async callback to DismissUIAndRollbackEditsAndEndRenameSessionAsync.
            // So everything in that method will happen synchronously.
            DismissUIAndRollbackEditsAndEndRenameSessionAsync(
                RenameLogMessage.UserActionOutcome.Canceled, previewChanges: false).Wait();
        }

        private async Task DismissUIAndRollbackEditsAndEndRenameSessionAsync(
            RenameLogMessage.UserActionOutcome outcome,
            bool previewChanges,
            Func<Task> finalCommitAction = null)
        {
            // Note: this entire sequence of steps is not cancellable.  We must perform it all to get back to a correct
            // state for all the editors the user is interacting with.
            var cancellationToken = CancellationToken.None;
            await _threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // Remove all our adornments and restore all buffer texts to their initial state.
            DismissUIAndRollbackEdits();

            // We're about to perform the final commit action.  No need to do any of our BG work to find-refs or compute conflicts.
            _cancellationTokenSource.Cancel();
            _conflictResolutionTaskCancellationSource.Cancel();

            // Close the keep alive session we have open with OOP, allowing it to release the solution it is holding onto.
            _keepAliveSession.Dispose();

            // Perform the actual commit step if we've been asked to.
            if (finalCommitAction != null)
            {
                // ConfigureAwait(true) so we come back to the UI thread to finish work.
                await finalCommitAction().ConfigureAwait(true);
            }

            // Log the result so we know how well rename is going in practice.
            LogRenameSession(outcome, previewChanges);

            // Remove all our rename trackers from the text buffer properties.
            RenameTrackingDismisser.DismissRenameTracking(_workspace, _workspace.GetOpenDocumentIds());

            // Log how long the full rename took.
            _inlineRenameSessionDurationLogBlock.Dispose();

            return;

            void DismissUIAndRollbackEdits()
            {
                _dismissed = true;
                _workspace.WorkspaceChanged -= OnWorkspaceChanged;
                _textBufferAssociatedViewService.SubjectBuffersConnected -= OnSubjectBuffersConnected;

                // Reenable completion now that the inline rename session is done
                _completionDisabledToken.Dispose();

                foreach (var textBuffer in _openTextBuffers.Keys)
                {
                    var document = textBuffer.CurrentSnapshot.GetOpenDocumentInCurrentContextWithChanges();
                    var isClosed = document == null;

                    var openBuffer = _openTextBuffers[textBuffer];
                    openBuffer.DisconnectAndRollbackEdits(isClosed);
                }

                this.UndoManager.Disconnect();

                if (_triggerView != null && !_triggerView.IsClosed)
                {
                    _triggerView.Selection.Clear();
                }

                RenameService.ActiveSession = null;
            }
        }

        public void Commit(bool previewChanges = false)
            => CommitWorker(previewChanges);

        /// <returns><see langword="true"/> if the rename operation was committed, <see
        /// langword="false"/> otherwise</returns>
        private bool CommitWorker(bool previewChanges)
        {
            // We're going to synchronously block the UI thread here.  So we can't use the background work indicator (as
            // it needs the UI thread to update itself.  This will force us to go through the Threaded-Wait-Dialog path
            // which at least will allow the user to cancel the rename if they want.
            //
            // In the future we should remove this entrypoint and have all callers use CommitAsync instead.
            return _threadingContext.JoinableTaskFactory.Run(() => CommitWorkerAsync(previewChanges, canUseBackgroundWorkIndicator: false, CancellationToken.None));
        }

        public Task CommitAsync(bool previewChanges, CancellationToken cancellationToken)
           => CommitWorkerAsync(previewChanges, canUseBackgroundWorkIndicator: true, cancellationToken);

        /// <returns><see langword="true"/> if the rename operation was commited, <see
        /// langword="false"/> otherwise</returns>
        private async Task<bool> CommitWorkerAsync(bool previewChanges, bool canUseBackgroundWorkIndicator, CancellationToken cancellationToken)
        {
            await _threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            VerifyNotDismissed();

            // If the identifier was deleted (or didn't change at all) then cancel the operation.
            // Note: an alternative approach would be for the work we're doing (like detecting
            // conflicts) to quickly bail in the case of no change.  However, that involves deeper
            // changes to the system and is less easy to validate that nothing happens.
            //
            // The only potential downside here would be if there was a language that wanted to
            // still 'rename' even if the identifier went away (or was unchanged).  But that isn't
            // a case we're aware of, so it's fine to be opinionated here that we can quickly bail
            // in these cases.
            if (this.ReplacementText == string.Empty ||
                this.ReplacementText == _initialRenameText)
            {
                Cancel();
                return false;
            }

            previewChanges = previewChanges || _previewChanges;

            try
            {
                if (canUseBackgroundWorkIndicator && this.RenameService.GlobalOptions.GetOption(InlineRenameSessionOptionsStorage.RenameAsynchronously))
                {
                    // We do not cancel on edit because as part of the rename system we have asynchronous work still
                    // occurring that itself may be asynchronously editing the buffer (for example, updating reference
                    // locations with the final renamed text).  Ideally though, once we start comitting, we would cancel
                    // any of that work and then only have the work of rolling back to the original state of the world
                    // and applying the desired edits ourselves.
                    var factory = _workspace.Services.GetRequiredService<IBackgroundWorkIndicatorFactory>();
                    using var context = factory.Create(
                        _triggerView, TriggerSpan, EditorFeaturesResources.Computing_Rename_information,
                        cancelOnEdit: false, cancelOnFocusLost: false);

                    await CommitCoreAsync(context, previewChanges).ConfigureAwait(true);
                }
                else
                {
                    using var context = _uiThreadOperationExecutor.BeginExecute(
                        title: EditorFeaturesResources.Rename,
                        defaultDescription: EditorFeaturesResources.Computing_Rename_information,
                        allowCancellation: true,
                        showProgress: false);

                    // .ConfigureAwait(true); so we can return to the UI thread to dispose the operation context.  It
                    // has a non-JTF threading dependency on the main thread.  So it can deadlock if you call it on a BG
                    // thread when in a blocking JTF call.
                    await CommitCoreAsync(context, previewChanges).ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException)
            {
                await DismissUIAndRollbackEditsAndEndRenameSessionAsync(
                    RenameLogMessage.UserActionOutcome.Canceled | RenameLogMessage.UserActionOutcome.Committed, previewChanges).ConfigureAwait(false);
                return false;
            }

            return true;
        }

        private async Task CommitCoreAsync(IUIThreadOperationContext operationContext, bool previewChanges)
        {
            var cancellationToken = operationContext.UserCancellationToken;
            var eventName = previewChanges ? FunctionId.Rename_CommitCoreWithPreview : FunctionId.Rename_CommitCore;
            using (Logger.LogBlock(eventName, KeyValueLogMessage.Create(LogType.UserAction), cancellationToken))
            {
                var info = await _conflictResolutionTask.JoinAsync(cancellationToken).ConfigureAwait(false);
                var newSolution = info.NewSolution;

                if (previewChanges)
                {
                    var previewService = _workspace.Services.GetService<IPreviewDialogService>();

                    newSolution = previewService.PreviewChanges(
                        string.Format(EditorFeaturesResources.Preview_Changes_0, EditorFeaturesResources.Rename),
                        "vs.csharp.refactoring.rename",
                        string.Format(EditorFeaturesResources.Rename_0_to_1_colon, this.OriginalSymbolName, this.ReplacementText),
                        _renameInfo.FullDisplayName,
                        _renameInfo.Glyph,
                        newSolution,
                        _triggerDocument.Project.Solution);

                    if (newSolution == null)
                    {
                        // User clicked cancel.
                        return;
                    }
                }

                // The user hasn't canceled by now, so we're done waiting for them. Off to rename!
                using var _ = operationContext.AddScope(allowCancellation: false, EditorFeaturesResources.Updating_files);

                await DismissUIAndRollbackEditsAndEndRenameSessionAsync(
                    RenameLogMessage.UserActionOutcome.Committed, previewChanges,
                    async () =>
                    {
                        var error = await TryApplyRenameAsync(newSolution, cancellationToken).ConfigureAwait(false);

                        if (error is not null)
                        {
                            await _threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                            var notificationService = _workspace.Services.GetService<INotificationService>();
                            notificationService.SendNotification(
                                error.Value.message, EditorFeaturesResources.Rename_Symbol, error.Value.severity);
                        }
                    }).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Returns non-null error message if renaming fails.
        /// </summary>
        private async Task<(NotificationSeverity severity, string message)?> TryApplyRenameAsync(
            Solution newSolution, CancellationToken cancellationToken)
        {
            var changes = _baseSolution.GetChanges(newSolution);
            var changedDocumentIDs = changes.GetProjectChanges().SelectMany(c => c.GetChangedDocuments()).ToList();

            await _threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (!_renameInfo.TryOnBeforeGlobalSymbolRenamed(_workspace, changedDocumentIDs, this.ReplacementText))
                return (NotificationSeverity.Error, EditorFeaturesResources.Rename_operation_was_cancelled_or_is_not_valid);

            using var undoTransaction = _workspace.OpenGlobalUndoTransaction(EditorFeaturesResources.Inline_Rename);

            await TaskScheduler.Default;
            var finalSolution = newSolution.Workspace.CurrentSolution;
            foreach (var id in changedDocumentIDs)
            {
                // If the document supports syntax tree, then create the new solution from the
                // updated syntax root.  This should ensure that annotations are preserved, and
                // prevents the solution from having to reparse documents when we already have
                // the trees for them.  If we don't support syntax, then just use the text of
                // the document.
                var newDocument = newSolution.GetDocument(id);

                if (newDocument.SupportsSyntaxTree)
                {
                    var root = await newDocument.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                    finalSolution = finalSolution.WithDocumentSyntaxRoot(id, root);
                }
                else
                {
                    var newText = await newDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
                    finalSolution = finalSolution.WithDocumentText(id, newText);
                }

                // Make sure to include any document rename as well
                finalSolution = finalSolution.WithDocumentName(id, newDocument.Name);
            }

            await _threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (!_workspace.TryApplyChanges(finalSolution))
                return (NotificationSeverity.Error, EditorFeaturesResources.Rename_operation_could_not_complete_due_to_external_change_to_workspace);

            try
            {
                // Since rename can apply file changes as well, and those file
                // changes can generate new document ids, include added documents
                // as well as changed documents. This also ensures that any document
                // that was removed is not included
                var finalChanges = _workspace.CurrentSolution.GetChanges(_baseSolution);

                var finalChangedIds = finalChanges
                    .GetProjectChanges()
                    .SelectMany(c => c.GetChangedDocuments().Concat(c.GetAddedDocuments()))
                    .ToList();

                if (!_renameInfo.TryOnAfterGlobalSymbolRenamed(_workspace, finalChangedIds, this.ReplacementText))
                    return (NotificationSeverity.Information, EditorFeaturesResources.Rename_operation_was_not_properly_completed_Some_file_might_not_have_been_updated);

                return null;
            }
            finally
            {
                // If we successfully updated the workspace then make sure the undo transaction is committed and is
                // always able to undo anything any other external listener did.
                undoTransaction.Commit();
            }
        }

        internal bool TryGetContainingEditableSpan(SnapshotPoint point, out SnapshotSpan editableSpan)
        {
            editableSpan = default;
            if (!_openTextBuffers.TryGetValue(point.Snapshot.TextBuffer, out var bufferManager))
            {
                return false;
            }

            foreach (var span in bufferManager.GetEditableSpansForSnapshot(point.Snapshot))
            {
                if (span.Contains(point) || span.End == point)
                {
                    editableSpan = span;
                    return true;
                }
            }

            return false;
        }

        internal bool IsInOpenTextBuffer(SnapshotPoint point)
            => _openTextBuffers.ContainsKey(point.Snapshot.TextBuffer);

        internal TestAccessor GetTestAccessor()
            => new TestAccessor(this);

        public struct TestAccessor
        {
            private readonly InlineRenameSession _inlineRenameSession;

            public TestAccessor(InlineRenameSession inlineRenameSession)
                => _inlineRenameSession = inlineRenameSession;

            public bool CommitWorker(bool previewChanges)
                => _inlineRenameSession.CommitWorker(previewChanges);
        }
    }
}
