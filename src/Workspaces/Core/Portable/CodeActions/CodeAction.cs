﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CaseCorrection;
using Microsoft.CodeAnalysis.CodeCleanup;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Tags;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CodeActions
{
    /// <summary>
    /// An action produced by a <see cref="CodeFixProvider"/> or a <see cref="CodeRefactoringProvider"/>.
    /// </summary>
    public abstract class CodeAction
    {
        /// <summary>
        /// Tag we use to convey that this code action should only be shown if it's in a host that allows for
        /// non-document changes.  For example if it needs to make project changes, or if will show host-specific UI.
        /// <para>
        /// Note: if the bulk of code action is just document changes, and it does some optional things beyond that
        /// (like navigating the user somewhere) this should not be set.  Such a code action is still usable in all
        /// hosts and should be shown to the user.  It's only if the code action can truly not function should this
        /// tag be provided.
        /// </para>
        /// <para>
        /// Currently, this also means that we presume that all 3rd party code actions do not require non-document
        /// changes and we will show them all in all hosts.
        /// </para>
        /// </summary>
        internal const string RequiresNonDocumentChange = nameof(RequiresNonDocumentChange);
        private protected static ImmutableArray<string> RequiresNonDocumentChangeTags = ImmutableArray.Create(RequiresNonDocumentChange);

        /// <summary>
        /// A short title describing the action that may appear in a menu.
        /// </summary>
        public abstract string Title { get; }

        internal virtual string Message => Title;

        /// <summary>
        /// Two code actions are treated as equivalent if they have equal non-null <see cref="EquivalenceKey"/> values and were generated
        /// by the same <see cref="CodeFixProvider"/> or <see cref="CodeRefactoringProvider"/>.
        /// </summary>
        /// <remarks>
        /// Equivalence of code actions affects some Visual Studio behavior. For example, if multiple equivalent
        /// code actions result from code fixes or refactorings for a single Visual Studio light bulb instance,
        /// the light bulb UI will present only one code action from each set of equivalent code actions.
        /// Additionally, a Fix All operation will apply only code actions that are equivalent to the original code action.
        ///
        /// If two code actions that could be treated as equivalent do not have equal <see cref="EquivalenceKey"/> values, Visual Studio behavior
        /// may be less helpful than would be optimal. If two code actions that should be treated as distinct have
        /// equal <see cref="EquivalenceKey"/> values, Visual Studio behavior may appear incorrect.
        /// </remarks>
        public virtual string? EquivalenceKey => null;

        internal virtual bool IsInlinable => false;

        internal virtual CodeActionPriority Priority => CodeActionPriority.Default;

        /// <summary>
        /// Descriptive tags from <see cref="WellKnownTags"/>.
        /// These tags may influence how the item is displayed.
        /// </summary>
        public virtual ImmutableArray<string> Tags => ImmutableArray<string>.Empty;

        internal virtual ImmutableArray<CodeAction> NestedCodeActions
            => ImmutableArray<CodeAction>.Empty;

        /// <summary>
        /// Gets custom tags for the CodeAction.
        /// </summary>
        internal ImmutableArray<string> CustomTags { get; private set; } = ImmutableArray<string>.Empty;

        /// <summary>
        /// Lazily set provider type that registered this code action.
        /// Used for telemetry purposes only.
        /// </summary>
        private Type? _providerTypeForTelemetry;

        /// <summary>
        /// Used by the CodeFixService and CodeRefactoringService to add the Provider Name as a CustomTag.
        /// </summary>
        internal void AddCustomTagAndTelemetryInfo(CodeChangeProviderMetadata? providerMetadata, object provider)
        {
            Contract.ThrowIfFalse(provider is CodeFixProvider or CodeRefactoringProvider);

            // Add the provider name to the parent CodeAction's CustomTags.
            // Always add a name even in cases of 3rd party fixers/refactorings that do not export
            // name metadata.
            var tag = providerMetadata?.Name ?? provider.GetTypeDisplayName();
            CustomTags = CustomTags.Add(tag);

            // Set the provider type to use for logging telemetry.
            _providerTypeForTelemetry = provider.GetType();
        }

        internal Guid GetTelemetryId(FixAllScope? fixAllScope = null)
        {
            // We need to identify the type name to use for CodeAction's telemetry ID.
            // For code actions created from 'CodeAction.Create' factory methods,
            // we use the provider type for telemetry.  For the rest of the code actions
            // created by sub-typing CodeAction type, we use the code action type for telemetry.
            // For the former case, if the provider type is not set, we fallback to the CodeAction type instead.
            var isFactoryGenerated = this is SimpleCodeAction { CreatedFromFactoryMethod: true };
            var type = isFactoryGenerated && _providerTypeForTelemetry != null
                ? _providerTypeForTelemetry
                : this.GetType();

            // Additionally, we also add the equivalence key and fixAllScope ID (if non-null)
            // to the telemetry ID.
            var scope = fixAllScope?.GetScopeIdForTelemetry() ?? 0;
            return type.GetTelemetryId(scope, EquivalenceKey);
        }

        /// <summary>
        /// The sequence of operations that define the code action.
        /// </summary>
        public Task<ImmutableArray<CodeActionOperation>> GetOperationsAsync(CancellationToken cancellationToken)
            => GetOperationsAsync(new ProgressTracker(), cancellationToken);

        internal Task<ImmutableArray<CodeActionOperation>> GetOperationsAsync(
            IProgressTracker progressTracker, CancellationToken cancellationToken)
        {
            return GetOperationsCoreAsync(progressTracker, cancellationToken);
        }

        /// <summary>
        /// The sequence of operations that define the code action.
        /// </summary>
        internal virtual async Task<ImmutableArray<CodeActionOperation>> GetOperationsCoreAsync(
            IProgressTracker progressTracker, CancellationToken cancellationToken)
        {
            var operations = await this.ComputeOperationsAsync(progressTracker, cancellationToken).ConfigureAwait(false);

            if (operations != null)
            {
                return await this.PostProcessAsync(operations, cancellationToken).ConfigureAwait(false);
            }

            return ImmutableArray<CodeActionOperation>.Empty;
        }

        /// <summary>
        /// The sequence of operations used to construct a preview.
        /// </summary>
        public async Task<ImmutableArray<CodeActionOperation>> GetPreviewOperationsAsync(CancellationToken cancellationToken)
        {
            var operations = await this.ComputePreviewOperationsAsync(cancellationToken).ConfigureAwait(false);

            if (operations != null)
            {
                return await this.PostProcessAsync(operations, cancellationToken).ConfigureAwait(false);
            }

            return ImmutableArray<CodeActionOperation>.Empty;
        }

        /// <summary>
        /// Override this method if you want to implement a <see cref="CodeAction"/> subclass that includes custom <see cref="CodeActionOperation"/>'s.
        /// </summary>
        protected virtual async Task<IEnumerable<CodeActionOperation>> ComputeOperationsAsync(CancellationToken cancellationToken)
        {
            var changedSolution = await GetChangedSolutionAsync(cancellationToken).ConfigureAwait(false);
            if (changedSolution == null)
            {
                return Array.Empty<CodeActionOperation>();
            }

            return new CodeActionOperation[] { new ApplyChangesOperation(changedSolution) };
        }

        internal virtual async Task<ImmutableArray<CodeActionOperation>> ComputeOperationsAsync(
            IProgressTracker progressTracker, CancellationToken cancellationToken)
        {
            var operations = await ComputeOperationsAsync(cancellationToken).ConfigureAwait(false);
            return operations.ToImmutableArrayOrEmpty();
        }

        /// <summary>
        /// Override this method if you want to implement a <see cref="CodeAction"/> that has a set of preview operations that are different
        /// than the operations produced by <see cref="ComputeOperationsAsync(CancellationToken)"/>.
        /// </summary>
        protected virtual Task<IEnumerable<CodeActionOperation>> ComputePreviewOperationsAsync(CancellationToken cancellationToken)
            => ComputeOperationsAsync(cancellationToken);

        /// <summary>
        /// Computes all changes for an entire solution.
        /// Override this method if you want to implement a <see cref="CodeAction"/> subclass that changes more than one document.
        /// </summary>
        protected virtual async Task<Solution?> GetChangedSolutionAsync(CancellationToken cancellationToken)
        {
            var changedDocument = await GetChangedDocumentAsync(cancellationToken).ConfigureAwait(false);
            if (changedDocument == null)
            {
                return null;
            }

            return changedDocument.Project.Solution;
        }

        internal virtual Task<Solution?> GetChangedSolutionAsync(
            IProgressTracker progressTracker, CancellationToken cancellationToken)
        {
            return GetChangedSolutionAsync(cancellationToken);
        }

        /// <summary>
        /// Computes changes for a single document. Override this method if you want to implement a
        /// <see cref="CodeAction"/> subclass that changes a single document.
        /// </summary>
        /// <remarks>
        /// All code actions are expected to operate on solutions. This method is a helper to simplify the
        /// implementation of <see cref="GetChangedSolutionAsync(CancellationToken)"/> for code actions that only need
        /// to change one document.
        /// </remarks>
        /// <exception cref="NotSupportedException">If this code action does not support changing a single document.</exception>
        protected virtual Task<Document> GetChangedDocumentAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException(GetType().FullName);

        /// <summary>
        /// used by batch fixer engine to get new solution
        /// </summary>
        internal async Task<Solution?> GetChangedSolutionInternalAsync(bool postProcessChanges = true, CancellationToken cancellationToken = default)
        {
            var solution = await GetChangedSolutionAsync(new ProgressTracker(), cancellationToken).ConfigureAwait(false);
            if (solution == null || !postProcessChanges)
            {
                return solution;
            }

            return await this.PostProcessChangesAsync(solution, cancellationToken).ConfigureAwait(false);
        }

        internal Task<Document> GetChangedDocumentInternalAsync(CancellationToken cancellation)
            => GetChangedDocumentAsync(cancellation);

        /// <summary>
        /// Apply post processing steps to any <see cref="ApplyChangesOperation"/>'s.
        /// </summary>
        /// <param name="operations">A list of operations.</param>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>A new list of operations with post processing steps applied to any <see cref="ApplyChangesOperation"/>'s.</returns>
        protected async Task<ImmutableArray<CodeActionOperation>> PostProcessAsync(IEnumerable<CodeActionOperation> operations, CancellationToken cancellationToken)
        {
            var arrayBuilder = new ArrayBuilder<CodeActionOperation>();

            foreach (var op in operations)
            {
                if (op is ApplyChangesOperation ac)
                {
                    arrayBuilder.Add(new ApplyChangesOperation(await this.PostProcessChangesAsync(ac.ChangedSolution, cancellationToken).ConfigureAwait(false)));
                }
                else
                {
                    arrayBuilder.Add(op);
                }
            }

            return arrayBuilder.ToImmutableAndFree();
        }

        /// <summary>
        ///  Apply post processing steps to solution changes, like formatting and simplification.
        /// </summary>
        /// <param name="changedSolution">The solution changed by the <see cref="CodeAction"/>.</param>
        /// <param name="cancellationToken">A cancellation token</param>
        protected async Task<Solution> PostProcessChangesAsync(Solution changedSolution, CancellationToken cancellationToken)
        {
            var solutionChanges = changedSolution.GetChanges(changedSolution.Workspace.CurrentSolution);

            var processedSolution = changedSolution;

            // process changed projects
            foreach (var projectChanges in solutionChanges.GetProjectChanges())
            {
                var documentsToProcess = projectChanges.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true).Concat(
                    projectChanges.GetAddedDocuments());

                foreach (var documentId in documentsToProcess)
                {
                    var document = processedSolution.GetRequiredDocument(documentId);
                    var processedDocument = await PostProcessChangesAsync(document, cancellationToken).ConfigureAwait(false);
                    processedSolution = processedDocument.Project.Solution;
                }
            }

            // process completely new projects too
            foreach (var addedProject in solutionChanges.GetAddedProjects())
            {
                var documentsToProcess = addedProject.DocumentIds;

                foreach (var documentId in documentsToProcess)
                {
                    var document = processedSolution.GetRequiredDocument(documentId);
                    var processedDocument = await PostProcessChangesAsync(document, cancellationToken).ConfigureAwait(false);
                    processedSolution = processedDocument.Project.Solution;
                }
            }

            return processedSolution;
        }

        /// <summary>
        /// Apply post processing steps to a single document:
        ///   Reducing nodes annotated with <see cref="Simplifier.Annotation"/>
        ///   Formatting nodes annotated with <see cref="Formatter.Annotation"/>
        /// </summary>
        /// <param name="document">The document changed by the <see cref="CodeAction"/>.</param>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>A document with the post processing changes applied.</returns>
        protected virtual async Task<Document> PostProcessChangesAsync(Document document, CancellationToken cancellationToken)
        {
            if (document.SupportsSyntaxTree)
            {
                // TODO: avoid ILegacyGlobalOptionsWorkspaceService https://github.com/dotnet/roslyn/issues/60777
                var globalOptions = document.Project.Solution.Services.GetService<ILegacyGlobalOptionsWorkspaceService>();
                var fallbackOptions = globalOptions?.CleanCodeGenerationOptionsProvider ?? CodeActionOptions.DefaultProvider;

                var options = await document.GetCodeCleanupOptionsAsync(fallbackOptions, cancellationToken).ConfigureAwait(false);
                return await CleanupDocumentAsync(document, options, cancellationToken).ConfigureAwait(false);
            }

            return document;
        }

        internal static async Task<Document> CleanupDocumentAsync(
            Document document, CodeCleanupOptions options, CancellationToken cancellationToken)
        {
            document = await ImportAdder.AddImportsFromSymbolAnnotationAsync(
                document, Simplifier.AddImportsAnnotation, options.AddImportOptions, cancellationToken).ConfigureAwait(false);

            document = await Simplifier.ReduceAsync(document, Simplifier.Annotation, options.SimplifierOptions, cancellationToken).ConfigureAwait(false);

            // format any node with explicit formatter annotation
            document = await Formatter.FormatAsync(document, Formatter.Annotation, options.FormattingOptions, cancellationToken).ConfigureAwait(false);

            // format any elastic whitespace
            document = await Formatter.FormatAsync(document, SyntaxAnnotation.ElasticAnnotation, options.FormattingOptions, cancellationToken).ConfigureAwait(false);

            document = await CaseCorrector.CaseCorrectAsync(document, CaseCorrector.Annotation, cancellationToken).ConfigureAwait(false);

            return document;
        }

        #region Factories for standard code actions

        /// <summary>
        /// Creates a <see cref="CodeAction"/> for a change to a single <see cref="Document"/>.
        /// Use this factory when the change is expensive to compute and should be deferred until requested.
        /// </summary>
        /// <param name="title">Title of the <see cref="CodeAction"/>.</param>
        /// <param name="createChangedDocument">Function to create the <see cref="Document"/>.</param>
        /// <param name="equivalenceKey">Optional value used to determine the equivalence of the <see cref="CodeAction"/> with other <see cref="CodeAction"/>s. See <see cref="CodeAction.EquivalenceKey"/>.</param>
        [SuppressMessage("ApiDesign", "RS0027:Public API with optional parameter(s) should have the most parameters amongst its public overloads.", Justification = "Preserving existing public API")]
        public static CodeAction Create(string title, Func<CancellationToken, Task<Document>> createChangedDocument, string? equivalenceKey = null)
        {
            if (title == null)
            {
                throw new ArgumentNullException(nameof(title));
            }

            if (createChangedDocument == null)
            {
                throw new ArgumentNullException(nameof(createChangedDocument));
            }

            return DocumentChangeAction.Create(title, createChangedDocument, equivalenceKey);
        }

        /// <summary>
        /// Creates a <see cref="CodeAction"/> for a change to more than one <see cref="Document"/> within a <see cref="Solution"/>.
        /// Use this factory when the change is expensive to compute and should be deferred until requested.
        /// </summary>
        /// <param name="title">Title of the <see cref="CodeAction"/>.</param>
        /// <param name="createChangedSolution">Function to create the <see cref="Solution"/>.</param>
        /// <param name="equivalenceKey">Optional value used to determine the equivalence of the <see cref="CodeAction"/> with other <see cref="CodeAction"/>s. See <see cref="CodeAction.EquivalenceKey"/>.</param>
        [SuppressMessage("ApiDesign", "RS0027:Public API with optional parameter(s) should have the most parameters amongst its public overloads.", Justification = "Preserving existing public API")]
        public static CodeAction Create(string title, Func<CancellationToken, Task<Solution>> createChangedSolution, string? equivalenceKey = null)
        {
            if (title == null)
            {
                throw new ArgumentNullException(nameof(title));
            }

            if (createChangedSolution == null)
            {
                throw new ArgumentNullException(nameof(createChangedSolution));
            }

            return SolutionChangeAction.Create(title, createChangedSolution, equivalenceKey);
        }

        /// <summary>
        /// Creates a <see cref="CodeAction"/> representing a group of code actions.
        /// </summary>
        /// <param name="title">Title of the <see cref="CodeAction"/> group.</param>
        /// <param name="nestedActions">The code actions within the group.</param>
        /// <param name="isInlinable"><see langword="true"/> to allow inlining the members of the group into the parent;
        /// otherwise, <see langword="false"/> to require that this group appear as a group with nested actions.</param>
        public static CodeAction Create(string title, ImmutableArray<CodeAction> nestedActions, bool isInlinable)
            => Create(title, nestedActions, isInlinable, priority: CodeActionPriority.Default);

        internal static CodeAction Create(string title, ImmutableArray<CodeAction> nestedActions, bool isInlinable, CodeActionPriority priority)
        {
            if (title is null)
                throw new ArgumentNullException(nameof(title));

            if (nestedActions == null)
                throw new ArgumentNullException(nameof(nestedActions));

            return CodeActionWithNestedActions.Create(title, nestedActions, isInlinable, priority);
        }

        internal static CodeAction CreateWithPriority(CodeActionPriority priority, string title, Func<CancellationToken, Task<Document>> createChangedDocument, string equivalenceKey)
            => DocumentChangeAction.Create(
                title ?? throw new ArgumentNullException(nameof(title)),
                createChangedDocument ?? throw new ArgumentNullException(nameof(createChangedDocument)),
                equivalenceKey ?? throw new ArgumentNullException(nameof(equivalenceKey)),
                priority);

        internal static CodeAction CreateWithPriority(CodeActionPriority priority, string title, Func<CancellationToken, Task<Solution>> createChangedSolution, string equivalenceKey)
            => SolutionChangeAction.Create(
                title ?? throw new ArgumentNullException(nameof(title)),
                createChangedSolution ?? throw new ArgumentNullException(nameof(createChangedSolution)),
                equivalenceKey ?? throw new ArgumentNullException(nameof(equivalenceKey)),
                priority);

        internal static CodeAction CreateWithPriority(CodeActionPriority priority, string title, ImmutableArray<CodeAction> nestedActions, bool isInlinable)
            => CodeActionWithNestedActions.Create(
                title ?? throw new ArgumentNullException(nameof(title)), nestedActions, isInlinable, priority);

        internal abstract class SimpleCodeAction : CodeAction
        {
            protected SimpleCodeAction(
                string title,
                string? equivalenceKey,
                CodeActionPriority priority,
                bool createdFromFactoryMethod)
            {
                Title = title;
                EquivalenceKey = equivalenceKey;
                Priority = priority;
                CreatedFromFactoryMethod = createdFromFactoryMethod;
            }

            public sealed override string Title { get; }
            public sealed override string? EquivalenceKey { get; }
            internal sealed override CodeActionPriority Priority { get; }

            /// <summary>
            /// Indicates if this CodeAction was created using one of the 'CodeAction.Create' factory methods.
            /// This is used in <see cref="GetTelemetryId(FixAllScope?)"/> to determine the appropriate type
            /// name to log in the CodeAction telemetry.
            /// </summary>
            public bool CreatedFromFactoryMethod { get; }
        }

        internal class CodeActionWithNestedActions : SimpleCodeAction
        {
            private CodeActionWithNestedActions(
                string title,
                ImmutableArray<CodeAction> nestedActions,
                bool isInlinable,
                CodeActionPriority priority,
                bool createdFromFactoryMethod)
                : base(title, ComputeEquivalenceKey(nestedActions), priority, createdFromFactoryMethod)
            {
                Debug.Assert(nestedActions.Length > 0);
                NestedCodeActions = nestedActions;
                IsInlinable = isInlinable;
            }

            protected CodeActionWithNestedActions(
               string title,
               ImmutableArray<CodeAction> nestedActions,
               bool isInlinable,
               CodeActionPriority priority = CodeActionPriority.Default)
               : this(title, nestedActions, isInlinable, priority, createdFromFactoryMethod: false)
            {
            }

            public static new CodeActionWithNestedActions Create(
               string title,
               ImmutableArray<CodeAction> nestedActions,
               bool isInlinable,
               CodeActionPriority priority = CodeActionPriority.Default)
                => new(title, nestedActions, isInlinable, priority, createdFromFactoryMethod: true);

            internal sealed override bool IsInlinable { get; }

            internal sealed override ImmutableArray<CodeAction> NestedCodeActions { get; }

            private static string? ComputeEquivalenceKey(ImmutableArray<CodeAction> nestedActions)
            {
                var equivalenceKey = StringBuilderPool.Allocate();
                try
                {
                    foreach (var action in nestedActions)
                    {
                        equivalenceKey.Append((action.EquivalenceKey ?? action.GetHashCode().ToString()) + ";");
                    }

                    return equivalenceKey.Length > 0 ? equivalenceKey.ToString() : null;
                }
                finally
                {
                    StringBuilderPool.ReturnAndFree(equivalenceKey);
                }
            }
        }

        internal class DocumentChangeAction : SimpleCodeAction
        {
            private readonly Func<CancellationToken, Task<Document>> _createChangedDocument;

            private DocumentChangeAction(
                string title,
                Func<CancellationToken, Task<Document>> createChangedDocument,
                string? equivalenceKey,
                CodeActionPriority priority,
                bool createdFromFactoryMethod)
                : base(title, equivalenceKey, priority, createdFromFactoryMethod)
            {
                _createChangedDocument = createChangedDocument;
            }

            protected DocumentChangeAction(
                string title,
                Func<CancellationToken, Task<Document>> createChangedDocument,
                string? equivalenceKey,
                CodeActionPriority priority = CodeActionPriority.Default)
                : this(title, createChangedDocument, equivalenceKey, priority, createdFromFactoryMethod: false)
            {
            }

            public static DocumentChangeAction Create(
                string title,
                Func<CancellationToken, Task<Document>> createChangedDocument,
                string? equivalenceKey,
                CodeActionPriority priority = CodeActionPriority.Default)
                => new(title, createChangedDocument, equivalenceKey, priority, createdFromFactoryMethod: true);

            protected sealed override Task<Document> GetChangedDocumentAsync(CancellationToken cancellationToken)
                => _createChangedDocument(cancellationToken);
        }

        internal class SolutionChangeAction : SimpleCodeAction
        {
            private readonly Func<CancellationToken, Task<Solution>> _createChangedSolution;

            protected SolutionChangeAction(
                string title,
                Func<CancellationToken, Task<Solution>> createChangedSolution,
                string? equivalenceKey,
                CodeActionPriority priority,
                bool createdFromFactoryMethod)
                : base(title, equivalenceKey, priority, createdFromFactoryMethod)
            {
                _createChangedSolution = createChangedSolution;
            }

            protected SolutionChangeAction(
                string title,
                Func<CancellationToken, Task<Solution>> createChangedSolution,
                string? equivalenceKey,
                CodeActionPriority priority = CodeActionPriority.Default)
                : this(title, createChangedSolution, equivalenceKey, priority, createdFromFactoryMethod: false)
            {
            }

            public static SolutionChangeAction Create(
                string title,
                Func<CancellationToken, Task<Solution>> createChangedSolution,
                string? equivalenceKey,
                CodeActionPriority priority = CodeActionPriority.Default)
                => new(title, createChangedSolution, equivalenceKey, priority, createdFromFactoryMethod: true);

            protected sealed override Task<Solution?> GetChangedSolutionAsync(CancellationToken cancellationToken)
                => _createChangedSolution(cancellationToken).AsNullable();
        }

        internal sealed class NoChangeAction : SimpleCodeAction
        {
            private NoChangeAction(
                string title,
                string? equivalenceKey,
                CodeActionPriority priority,
                bool createdFromFactoryMethod)
                : base(title, equivalenceKey, priority, createdFromFactoryMethod)
            {
            }

            public static NoChangeAction Create(
                string title,
                string? equivalenceKey,
                CodeActionPriority priority = CodeActionPriority.Default)
                => new(title, equivalenceKey, priority, createdFromFactoryMethod: true);

            protected sealed override Task<Solution?> GetChangedSolutionAsync(CancellationToken cancellationToken)
                => SpecializedTasks.Null<Solution>();
        }

        #endregion
    }
}
