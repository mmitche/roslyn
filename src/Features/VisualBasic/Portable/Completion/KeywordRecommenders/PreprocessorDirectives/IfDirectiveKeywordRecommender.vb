﻿' Licensed to the .NET Foundation under one or more agreements.
' The .NET Foundation licenses this file to you under the MIT license.
' See the LICENSE file in the project root for more information.

Imports System.Collections.Immutable
Imports System.Threading
Imports Microsoft.CodeAnalysis.Completion.Providers
Imports Microsoft.CodeAnalysis.VisualBasic.Extensions.ContextQuery

Namespace Microsoft.CodeAnalysis.VisualBasic.Completion.KeywordRecommenders.PreprocessorDirectives
    ''' <summary>
    ''' Recommends the "#If" preprocessor directive
    ''' </summary>
    Friend Class IfDirectiveKeywordRecommender
        Inherits AbstractKeywordRecommender

        Protected Overrides Function RecommendKeywords(context As VisualBasicSyntaxContext, cancellationToken As CancellationToken) As ImmutableArray(Of RecommendedKeyword)
            If context.IsPreprocessorStartContext AndAlso Not context.SyntaxTree.IsEnumMemberNameContext(context) Then
                Return ImmutableArray.Create(New RecommendedKeyword("#If", VBFeaturesResources.Conditionally_compiles_selected_blocks_of_code_depending_on_the_value_of_an_expression))
            End If

            Return ImmutableArray(Of RecommendedKeyword).Empty
        End Function
    End Class
End Namespace
