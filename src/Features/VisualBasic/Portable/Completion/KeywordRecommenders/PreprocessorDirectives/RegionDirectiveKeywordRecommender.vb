﻿' Licensed to the .NET Foundation under one or more agreements.
' The .NET Foundation licenses this file to you under the MIT license.
' See the LICENSE file in the project root for more information.

Imports System.Collections.Immutable
Imports System.Threading
Imports Microsoft.CodeAnalysis.Completion.Providers
Imports Microsoft.CodeAnalysis.VisualBasic.Extensions.ContextQuery

Namespace Microsoft.CodeAnalysis.VisualBasic.Completion.KeywordRecommenders.PreprocessorDirectives
    ''' <summary>
    ''' Recommends the "#Region" directive
    ''' </summary>
    Friend Class RegionDirectiveKeywordRecommender
        Inherits AbstractKeywordRecommender

        Protected Overrides Function RecommendKeywords(context As VisualBasicSyntaxContext, cancellationToken As CancellationToken) As ImmutableArray(Of RecommendedKeyword)
            If context.IsPreprocessorStartContext AndAlso Not context.SyntaxTree.IsEnumMemberNameContext(context) Then
                Return ImmutableArray.Create(New RecommendedKeyword("#Region", VBFeaturesResources.Collapses_and_hides_sections_of_code_in_Visual_Basic_files))
            End If

            Return ImmutableArray(Of RecommendedKeyword).Empty
        End Function
    End Class
End Namespace
