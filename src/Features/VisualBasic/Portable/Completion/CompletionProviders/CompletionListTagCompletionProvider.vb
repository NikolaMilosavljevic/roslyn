﻿' Licensed to the .NET Foundation under one or more agreements.
' The .NET Foundation licenses this file to you under the MIT license.
' See the LICENSE file in the project root for more information.

Imports System.Collections.Immutable
Imports System.Composition
Imports System.Threading
Imports Microsoft.CodeAnalysis.Completion
Imports Microsoft.CodeAnalysis.Completion.Providers
Imports Microsoft.CodeAnalysis.Host.Mef
Imports Microsoft.CodeAnalysis.LanguageService
Imports Microsoft.CodeAnalysis.Options
Imports Microsoft.CodeAnalysis.VisualBasic.Extensions.ContextQuery

Namespace Microsoft.CodeAnalysis.VisualBasic.Completion.Providers
    <ExportCompletionProvider(NameOf(CompletionListTagCompletionProvider), LanguageNames.VisualBasic)>
    <ExtensionOrder(After:=NameOf(CrefCompletionProvider))>
    <[Shared]>
    Friend Class CompletionListTagCompletionProvider
        Inherits EnumCompletionProvider

        <ImportingConstructor>
        <Obsolete(MefConstruction.ImportingConstructorMessage, True)>
        Public Sub New()
            MyBase.New()
        End Sub

        Protected Overrides Async Function GetSymbolsAsync(
                completionContext As CompletionContext,
                syntaxContext As VisualBasicSyntaxContext,
                position As Integer,
                options As CompletionOptions,
                cancellationToken As CancellationToken) As Task(Of ImmutableArray(Of (symbol As ISymbol, preselect As Boolean)))

            Dim symbols = Await GetPreselectedSymbolsAsync(syntaxContext, position, options, cancellationToken).ConfigureAwait(False)
            Return symbols.SelectAsArray(Function(s) (s, preselect:=True))
        End Function

        Private Shared Function GetPreselectedSymbolsAsync(context As VisualBasicSyntaxContext, position As Integer, options As CompletionOptions, cancellationToken As CancellationToken) As Task(Of ImmutableArray(Of ISymbol))
            If context.SyntaxTree.IsObjectCreationTypeContext(position, cancellationToken) OrElse
                context.SyntaxTree.IsInNonUserCode(position, cancellationToken) Then
                Return SpecializedTasks.EmptyImmutableArray(Of ISymbol)()
            End If

            If context.TargetToken.IsKind(SyntaxKind.DotToken) Then
                Return SpecializedTasks.EmptyImmutableArray(Of ISymbol)()
            End If

            Dim typeInferenceService = context.GetLanguageService(Of ITypeInferenceService)()
            Dim inferredType = typeInferenceService.InferType(context.SemanticModel, position, objectAsDefault:=True, cancellationToken:=cancellationToken)
            If inferredType Is Nothing Then
                Return SpecializedTasks.EmptyImmutableArray(Of ISymbol)()
            End If

            Dim within = context.SemanticModel.GetEnclosingNamedType(position, cancellationToken)
            Dim completionListType = GetCompletionListType(inferredType, within, context.SemanticModel.Compilation, cancellationToken)

            If completionListType Is Nothing Then
                Return SpecializedTasks.EmptyImmutableArray(Of ISymbol)()
            End If

            Return Task.FromResult(completionListType.GetAccessibleMembersInThisAndBaseTypes(Of ISymbol)(within) _
                                                .WhereAsArray(Function(m) m.MatchesKind(SymbolKind.Field, SymbolKind.Property) AndAlso
                                                                    m.IsStatic AndAlso
                                                                    m.IsAccessibleWithin(within) AndAlso
                                                                    m.IsEditorBrowsable(options.HideAdvancedMembers, context.SemanticModel.Compilation)))
        End Function

        Private Shared Function GetCompletionListType(inferredType As ITypeSymbol, within As INamedTypeSymbol, compilation As Compilation, cancellationToken As CancellationToken) As ITypeSymbol
            Dim documentation = inferredType.GetDocumentationComment(compilation, expandIncludes:=True, expandInheritdoc:=True, cancellationToken:=cancellationToken)
            If documentation.CompletionListCref IsNot Nothing Then
                Dim crefType = DocumentationCommentId.GetSymbolsForDeclarationId(documentation.CompletionListCref, compilation) _
                                    .OfType(Of INamedTypeSymbol) _
                                    .FirstOrDefault()

                If crefType IsNot Nothing AndAlso crefType.IsAccessibleWithin(within) Then
                    Return crefType
                End If
            End If

            Return Nothing
        End Function

        Protected Overrides Function GetDisplayAndSuffixAndInsertionText(symbol As ISymbol, context As VisualBasicSyntaxContext) As (displayText As String, suffix As String, insertionText As String)
            Dim displayFormat = SymbolDisplayFormat.MinimallyQualifiedFormat.WithMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType).WithKindOptions(SymbolDisplayKindOptions.None)
            Dim text = symbol.ToMinimalDisplayString(context.SemanticModel, context.Position, displayFormat)
            Return (text, "", text)
        End Function

        Protected Overrides Function CreateItem(
                completionContext As CompletionContext,
                displayText As String,
                displayTextSuffix As String,
                insertionText As String,
                symbols As ImmutableArray(Of (symbol As ISymbol, preselect As Boolean)),
                context As VisualBasicSyntaxContext,
                supportedPlatformData As SupportedPlatformData) As CompletionItem

            Return SymbolCompletionItem.CreateWithSymbolId(
                displayText:=displayText,
                displayTextSuffix:=displayTextSuffix,
                insertionText:=insertionText,
                filterText:=GetFilterText(symbols(0).symbol, displayText, context),
                symbols:=symbols.SelectAsArray(Function(t) t.symbol),
                rules:=CompletionItemRules.Default.WithMatchPriority(MatchPriority.Preselect),
                contextPosition:=context.Position,
                sortText:=displayText,
                supportedPlatforms:=supportedPlatformData)
        End Function
    End Class
End Namespace
