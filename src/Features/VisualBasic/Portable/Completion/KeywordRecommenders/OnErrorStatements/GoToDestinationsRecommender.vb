﻿' Licensed to the .NET Foundation under one or more agreements.
' The .NET Foundation licenses this file to you under the MIT license.
' See the LICENSE file in the project root for more information.

Imports System.Collections.Immutable
Imports System.Threading
Imports Microsoft.CodeAnalysis.Completion.Providers
Imports Microsoft.CodeAnalysis.VisualBasic.Extensions.ContextQuery
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax

Namespace Microsoft.CodeAnalysis.VisualBasic.Completion.KeywordRecommenders.OnErrorStatements
    ''' <summary>
    ''' Recommends 0 and -1 as the "destinations" of where to go to after On Error Goto
    ''' </summary>
    Friend Class GoToDestinationsRecommender
        Inherits AbstractKeywordRecommender

        Protected Overrides Function RecommendKeywords(context As VisualBasicSyntaxContext, cancellationToken As CancellationToken) As ImmutableArray(Of RecommendedKeyword)
            If context.FollowsEndOfStatement Then
                Return ImmutableArray(Of RecommendedKeyword).Empty
            End If

            Dim targetToken = context.TargetToken

            If targetToken.Kind = SyntaxKind.GoToKeyword AndAlso TypeOf targetToken.Parent Is OnErrorGoToStatementSyntax Then
                Return ImmutableArray.Create(New RecommendedKeyword("0"), New RecommendedKeyword("-1"))
            Else
                Return ImmutableArray(Of RecommendedKeyword).Empty
            End If
        End Function
    End Class
End Namespace
