﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeFixesAndRefactorings;
using Microsoft.CodeAnalysis.Editor.Implementation.Suggestions;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Roslyn.Utilities;
using OLECMDEXECOPT = Microsoft.VisualStudio.OLE.Interop.OLECMDEXECOPT;

namespace Microsoft.VisualStudio.IntegrationTest.Utilities.InProcess
{
    internal abstract class TextViewWindow_InProc : InProcComponent
    {
        /// <remarks>
        /// This method does not wait for async operations before
        /// querying the editor
        /// </remarks>
        public string[] GetCompletionItems()
            => ExecuteOnActiveView(view =>
            {
                var broker = GetComponentModelService<ICompletionBroker>();

                var sessions = broker.GetSessions(view);
                if (sessions.Count != 1)
                {
                    throw new InvalidOperationException($"Expected exactly one session in the completion list, but found {sessions.Count}");
                }

                var selectedCompletionSet = sessions[0].SelectedCompletionSet;

                return selectedCompletionSet.Completions.Select(c => c.DisplayText).ToArray();
            });

        /// <remarks>
        /// This method does not wait for async operations before
        /// querying the editor
        /// </remarks>
        public string GetCurrentCompletionItem()
            => ExecuteOnActiveView(view =>
            {
                var broker = GetComponentModelService<ICompletionBroker>();

                var sessions = broker.GetSessions(view);
                if (sessions.Count != 1)
                {
                    throw new InvalidOperationException($"Expected exactly one session in the completion list, but found {sessions.Count}");
                }

                var selectedCompletionSet = sessions[0].SelectedCompletionSet;
                return selectedCompletionSet.SelectionStatus.Completion.DisplayText;
            });

        public void ShowLightBulb()
        {
            InvokeOnUIThread(cancellationToken =>
            {
                var shell = GetGlobalService<SVsUIShell, IVsUIShell>();
                var cmdGroup = typeof(VSConstants.VSStd14CmdID).GUID;
                var cmdExecOpt = OLECMDEXECOPT.OLECMDEXECOPT_DONTPROMPTUSER;

                var cmdID = VSConstants.VSStd14CmdID.ShowQuickFixes;
                object? obj = null;
                shell.PostExecCommand(cmdGroup, (uint)cmdID, (uint)cmdExecOpt, ref obj);
            });
        }

        public void WaitForLightBulbSession()
        {
            JoinableTaskFactory.Run(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();

                var view = GetActiveTextView();
                var broker = GetComponentModel().GetService<ILightBulbBroker>();
                await LightBulbHelper.WaitForLightBulbSessionAsync(broker, view);
            });
        }

        /// <remarks>
        /// This method does not wait for async operations before
        /// querying the editor
        /// </remarks>
        public bool IsCompletionActive()
        {
            if (!HasActiveTextView())
            {
                return false;
            }

            return ExecuteOnActiveView(view =>
            {
                var broker = GetComponentModelService<ICompletionBroker>();
                return broker.IsCompletionActive(view);
            });
        }

        protected abstract ITextBuffer GetBufferContainingCaret(IWpfTextView view);

        public string[] GetCurrentClassifications()
            => InvokeOnUIThread(cancellationToken =>
            {
                IClassifier? classifier = null;
                try
                {
                    var textView = GetActiveTextView();
                    var selectionSpan = textView.Selection.StreamSelectionSpan.SnapshotSpan;
                    if (selectionSpan.Length == 0)
                    {
                        var textStructureNavigatorSelectorService = GetComponentModelService<ITextStructureNavigatorSelectorService>();
                        selectionSpan = textStructureNavigatorSelectorService
                            .GetTextStructureNavigator(textView.TextBuffer)
                            .GetExtentOfWord(selectionSpan.Start).Span;
                    }

                    var classifierAggregatorService = GetComponentModelService<IViewClassifierAggregatorService>();
                    classifier = classifierAggregatorService.GetClassifier(textView);
                    var classifiedSpans = classifier.GetClassificationSpans(selectionSpan);
                    return classifiedSpans.Select(x => x.ClassificationType.Classification).ToArray();
                }
                finally
                {
                    if (classifier is IDisposable classifierDispose)
                    {
                        classifierDispose.Dispose();
                    }
                }
            });

        public int GetVisibleColumnCount()
        {
            return ExecuteOnActiveView(view =>
            {
                return (int)Math.Ceiling(view.ViewportWidth / Math.Max(view.FormattedLineSource.ColumnWidth, 1));
            });
        }

        public void PlaceCaret(
            string marker,
            int charsOffset,
            int occurrence,
            bool extendSelection,
            bool selectBlock)
            => ExecuteOnActiveView(view =>
            {
                var dte = GetDTE();
                dte.Find.FindWhat = marker;
                dte.Find.MatchCase = true;
                dte.Find.MatchInHiddenText = true;
                dte.Find.Target = EnvDTE.vsFindTarget.vsFindTargetCurrentDocument;
                dte.Find.Action = EnvDTE.vsFindAction.vsFindActionFind;

                var originalPosition = GetCaretPosition();
                view.Caret.MoveTo(new SnapshotPoint(GetBufferContainingCaret(view).CurrentSnapshot, 0));

                if (occurrence > 0)
                {
                    var result = EnvDTE.vsFindResult.vsFindResultNotFound;
                    for (var i = 0; i < occurrence; i++)
                    {
                        result = dte.Find.Execute();
                    }

                    if (result != EnvDTE.vsFindResult.vsFindResultFound)
                    {
                        throw new Exception("Occurrence " + occurrence + " of marker '" + marker + "' not found in text: " + view.TextSnapshot.GetText());
                    }
                }
                else
                {
                    var result = dte.Find.Execute();
                    if (result != EnvDTE.vsFindResult.vsFindResultFound)
                    {
                        throw new Exception("Marker '" + marker + "' not found in text: " + view.TextSnapshot.GetText());
                    }
                }

                if (charsOffset > 0)
                {
                    for (var i = 0; i < charsOffset - 1; i++)
                    {
                        view.Caret.MoveToNextCaretPosition();
                    }

                    view.Selection.Clear();
                }

                if (charsOffset < 0)
                {
                    // On the first negative charsOffset, move to anchor-point position, as if the user hit the LEFT key
                    view.Caret.MoveTo(new SnapshotPoint(view.TextSnapshot, view.Selection.AnchorPoint.Position.Position));

                    for (var i = 0; i < -charsOffset - 1; i++)
                    {
                        view.Caret.MoveToPreviousCaretPosition();
                    }

                    view.Selection.Clear();
                }

                if (extendSelection)
                {
                    var newPosition = view.Selection.ActivePoint.Position.Position;
                    view.Selection.Select(new VirtualSnapshotPoint(view.TextSnapshot, originalPosition), new VirtualSnapshotPoint(view.TextSnapshot, newPosition));
                    view.Selection.Mode = selectBlock ? TextSelectionMode.Box : TextSelectionMode.Stream;
                }
            });

        public int GetCaretPosition()
            => ExecuteOnActiveView(view =>
            {
                var subjectBuffer = GetBufferContainingCaret(view);
                var bufferPosition = view.Caret.Position.BufferPosition;
                return bufferPosition.Position;
            });

        public int GetCaretColumn()
        {
            return ExecuteOnActiveView(view =>
            {
                var startOfLine = view.Caret.ContainingTextViewLine.Start.Position;
                var caretVirtualPosition = view.Caret.Position.VirtualBufferPosition;
                return caretVirtualPosition.Position - startOfLine + caretVirtualPosition.VirtualSpaces;
            });
        }

        protected T ExecuteOnActiveView<T>(Func<IWpfTextView, T> action)
            => InvokeOnUIThread(cancellationToken =>
            {
                var view = GetActiveTextView();
                return action(view);
            });

        protected void ExecuteOnActiveView(Action<IWpfTextView> action)
            => InvokeOnUIThread(GetExecuteOnActionViewCallback(action));

        protected Action<CancellationToken> GetExecuteOnActionViewCallback(Action<IWpfTextView> action)
            => cancellationToken =>
            {
                var view = GetActiveTextView();
                action(view);
            };

        public void InvokeQuickInfo()
        {
            JoinableTaskFactory.Run(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();

                var broker = GetComponentModelService<IAsyncQuickInfoBroker>();
                var session = await broker.TriggerQuickInfoAsync(GetActiveTextView());
                Contract.ThrowIfNull(session);
            });
        }

        public string GetQuickInfo()
        {
            return JoinableTaskFactory.Run(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();

                var view = GetActiveTextView();
                var broker = GetComponentModelService<IAsyncQuickInfoBroker>();

                var session = broker.GetSession(view);

                // GetSession will not return null if preceded by a call to InvokeQuickInfo
                Contract.ThrowIfNull(session);

                using var cts = new CancellationTokenSource(Helper.HangMitigatingTimeout);
                while (session.State != QuickInfoSessionState.Visible)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    await Task.Delay(50, cts.Token).ConfigureAwait(true);
                }

                return QuickInfoToStringConverter.GetStringFromBulkContent(session.Content);
            });
        }

        public void VerifyTags(string tagTypeName, int expectedCount)
            => ExecuteOnActiveView(view =>
        {
            var type = WellKnownTagNames.GetTagTypeByName(tagTypeName);
            bool filterTag(IMappingTagSpan<ITag> tag)
            {
                return tag.Tag.GetType().Equals(type);
            }

            var service = GetComponentModelService<IViewTagAggregatorFactoryService>();
            var aggregator = service.CreateTagAggregator<ITag>(view);
            var allTags = aggregator.GetTags(new SnapshotSpan(view.TextSnapshot, 0, view.TextSnapshot.Length));
            var tags = allTags.Where(filterTag).Cast<IMappingTagSpan<ITag>>();
            var actualCount = tags.Count();

            if (expectedCount != actualCount)
            {
                var tagsTypesString = string.Join(",", allTags.Select(tag => tag.Tag.ToString()));
                throw new Exception($"Failed to verify {tagTypeName} tags. Expected count: {expectedCount}, Actual count: {actualCount}. All tags: {tagsTypesString}");
            }
        });

        public bool IsLightBulbSessionExpanded()
       => ExecuteOnActiveView(view =>
       {
           var broker = GetComponentModel().GetService<ILightBulbBroker>();

           if (!broker.IsLightBulbSessionActive(view))
           {
               return false;
           }

           var session = broker.GetSession(view);
           if (session == null || !session.IsExpanded)
           {
               return false;
           }

           return true;
       });

        public string[] GetLightBulbActions()
        {
            return JoinableTaskFactory.Run(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();

                var view = GetActiveTextView();
                var broker = GetComponentModel().GetService<ILightBulbBroker>();
                return (await GetLightBulbActionsAsync(broker, view)).Select(a => a.DisplayText).ToArray();
            });
        }

        private async Task<IEnumerable<ISuggestedAction>> GetLightBulbActionsAsync(ILightBulbBroker broker, IWpfTextView view)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            if (!broker.IsLightBulbSessionActive(view))
            {
                var bufferType = view.TextBuffer.ContentType.DisplayName;
                throw new Exception(string.Format("No light bulb session in View!  Buffer content type={0}", bufferType));
            }

            var activeSession = broker.GetSession(view);
            if (activeSession == null)
            {
                var bufferType = view.TextBuffer.ContentType.DisplayName;
                throw new InvalidOperationException(string.Format("No expanded light bulb session found after View.ShowSmartTag.  Buffer content type={0}", bufferType));
            }

            var actionSets = await LightBulbHelper.WaitForItemsAsync(broker, view);
            return await SelectActionsAsync(actionSets);
        }

        public bool ApplyLightBulbAction(string actionName, FixAllScope? fixAllScope, bool blockUntilComplete)
        {
            var lightBulbAction = GetLightBulbApplicationAction(actionName, fixAllScope, blockUntilComplete);
            var task = JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();

                var activeTextView = GetActiveTextView();
                return await lightBulbAction(activeTextView);
            });

            if (blockUntilComplete)
            {
                var result = task.Join();
                DismissLightBulbSession();
                return result;
            }

            return true;
        }

        private Func<IWpfTextView, Task<bool>> GetLightBulbApplicationAction(string actionName, FixAllScope? fixAllScope, bool willBlockUntilComplete)
        {
            return async view =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();

                var broker = GetComponentModel().GetService<ILightBulbBroker>();

                var actions = (await GetLightBulbActionsAsync(broker, view)).ToArray();
                var action = actions.FirstOrDefault(a => a.DisplayText == actionName);

                if (action == null)
                {
                    var sb = new StringBuilder();
                    foreach (var item in actions)
                    {
                        sb.AppendLine("Actual ISuggestedAction: " + item.DisplayText);
                    }

                    var bufferType = view.TextBuffer.ContentType.DisplayName;
                    throw new InvalidOperationException(
                        string.Format("ISuggestedAction {0} not found.  Buffer content type={1}\r\nActions: {2}", actionName, bufferType, sb.ToString()));
                }

                if (fixAllScope != null)
                {
                    if (!action.HasActionSets)
                    {
                        throw new InvalidOperationException($"Suggested action '{action.DisplayText}' does not support FixAllOccurrences.");
                    }

                    var actionSetsForAction = await action.GetActionSetsAsync(CancellationToken.None);
                    var fixAllAction = await GetFixAllSuggestedActionAsync(JoinableTaskFactory, actionSetsForAction, fixAllScope.Value);
                    if (fixAllAction == null)
                    {
                        throw new InvalidOperationException($"Unable to find FixAll in {fixAllScope.ToString()} code fix for suggested action '{action.DisplayText}'.");
                    }

                    action = fixAllAction;

                    if (willBlockUntilComplete
                        && action is AbstractFixAllSuggestedAction fixAllSuggestedAction
                        && fixAllSuggestedAction.CodeAction is AbstractFixAllCodeAction fixAllCodeAction)
                    {
                        // Ensure the preview changes dialog will not be shown. Since the operation 'willBlockUntilComplete',
                        // the caller would not be able to interact with the preview changes dialog, and the tests would
                        // either timeout or deadlock.
                        fixAllCodeAction.GetTestAccessor().ShowPreviewChangesDialog = false;
                    }

                    if (string.IsNullOrEmpty(actionName))
                    {
                        return false;
                    }

                    // Dismiss the lightbulb session as we not invoking the original code fix.
                    broker.DismissSession(view);
                }

                if (action is not SuggestedAction suggestedAction)
                    return true;

                broker.DismissSession(view);
                var threadOperationExecutor = GetComponentModelService<IUIThreadOperationExecutor>();
                var guardedOperations = GetComponentModelService<IGuardedOperations2>();
                threadOperationExecutor.Execute(
                    title: "Execute Suggested Action",
                    defaultDescription: Accelerator.StripAccelerators(action.DisplayText, '_'),
                    allowCancellation: true,
                    showProgress: true,
                    action: context =>
                    {
                        guardedOperations.CallExtensionPoint(
                            errorSource: suggestedAction,
                            call: () => suggestedAction.Invoke(context),
                            exceptionGuardFilter: e => e is not OperationCanceledException);
                    });

                return true;
            };
        }

        private async Task<IEnumerable<ISuggestedAction>> SelectActionsAsync(IEnumerable<SuggestedActionSet> actionSets)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            var actions = new List<ISuggestedAction>();

            if (actionSets != null)
            {
                foreach (var actionSet in actionSets)
                {
                    if (actionSet.Actions != null)
                    {
                        foreach (var action in actionSet.Actions)
                        {
                            actions.Add(action);
                            var nestedActionSets = await action.GetActionSetsAsync(CancellationToken.None);
                            var nestedActions = await SelectActionsAsync(nestedActionSets);
                            actions.AddRange(nestedActions);
                        }
                    }
                }
            }

            return actions;
        }

        private static async Task<AbstractFixAllSuggestedAction?> GetFixAllSuggestedActionAsync(JoinableTaskFactory joinableTaskFactory, IEnumerable<SuggestedActionSet> actionSets, FixAllScope fixAllScope)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            foreach (var actionSet in actionSets)
            {
                foreach (var action in actionSet.Actions)
                {
                    if (action is AbstractFixAllSuggestedAction fixAllSuggestedAction)
                    {
                        var fixAllCodeAction = fixAllSuggestedAction.CodeAction as AbstractFixAllCodeAction;
                        if (fixAllCodeAction?.FixAllState?.Scope == fixAllScope)
                        {
                            return fixAllSuggestedAction;
                        }
                    }

                    if (action.HasActionSets)
                    {
                        var nestedActionSets = await action.GetActionSetsAsync(CancellationToken.None);
                        var fixAllCodeAction = await GetFixAllSuggestedActionAsync(joinableTaskFactory, nestedActionSets, fixAllScope);
                        if (fixAllCodeAction != null)
                        {
                            return fixAllCodeAction;
                        }
                    }
                }
            }

            return null;
        }

        public void DismissLightBulbSession()
            => ExecuteOnActiveView(view =>
            {
                var broker = GetComponentModel().GetService<ILightBulbBroker>();
                broker.DismissSession(view);
            });

        public void DismissCompletionSessions()
            => ExecuteOnActiveView(view =>
            {
                var broker = GetComponentModel().GetService<ICompletionBroker>();
                broker.DismissAllSessions(view);
            });

        protected abstract bool HasActiveTextView();

        protected abstract IWpfTextView GetActiveTextView();
    }
}
