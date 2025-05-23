﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.PowerFx.Core;
using Microsoft.PowerFx.Core.Functions;
using Microsoft.PowerFx.Core.Tests;
using Microsoft.PowerFx.Core.Texl;
using Microsoft.PowerFx.Core.Texl.Builtins;
using Microsoft.PowerFx.Core.Types;
using Microsoft.PowerFx.Core.Types.Enums;
using Microsoft.PowerFx.Core.Utils;
using Microsoft.PowerFx.Types;
using Xunit;

namespace Microsoft.PowerFx.Tests.IntellisenseTests
{
    public class SuggestTests : IntellisenseTestBase
    {
        /// <summary>
        /// This method does the same as <see cref="Suggest"/>, but filters the suggestions by their text so
        /// that they can be more easily compared.
        /// </summary>
        /// <param name="expression">
        /// Test case wherein the presence of the `|` character indicates cursor position.  See
        /// <see cref="TestSuggest"/> for more details.
        /// </param>
        /// <param name="config"></param>
        /// <param name="culture"></param>
        /// <param name="contextTypeString">
        /// The type that defines names and types that are valid in <see cref="expression"/>.
        /// </param>
        /// <returns>
        /// List of string representing suggestions.
        /// </returns>
        private string[] SuggestStrings(string expression, PowerFxConfig config, CultureInfo culture = null, string contextTypeString = null)
        {
            Assert.NotNull(expression);

            var intellisense = Suggest(expression, config, culture ?? CultureInfo.InvariantCulture, contextTypeString);
            return intellisense.Suggestions.Select(suggestion => suggestion.DisplayText.Text).ToArray();
        }

        private string[] SuggestStrings(string expression, PowerFxConfig config, CultureInfo culture, RecordType context)
        {
            Assert.NotNull(expression);

            var intellisense = Suggest(expression, config, culture ?? CultureInfo.InvariantCulture, context);
            return intellisense.Suggestions.Select(suggestion => suggestion.DisplayText.Text).ToArray();
        }

        private string[] SuggestStrings(string expression, PowerFxConfig config, CultureInfo culture, ReadOnlySymbolTable symTable)
        {
            Assert.NotNull(expression);

            var intellisense = Suggest(expression, config, culture ?? CultureInfo.InvariantCulture, symTable);
            return intellisense.Suggestions.Select(suggestion => suggestion.DisplayText.Text).ToArray();
        }

        internal static PowerFxConfig Default => PowerFxConfig.BuildWithEnumStore(new EnumStoreBuilder().WithDefaultEnums());

        internal static PowerFxConfig Default_DisableRowScopeDisambiguationSyntax => PowerFxConfig.BuildWithEnumStore(new EnumStoreBuilder().WithDefaultEnums(), new Features { DisableRowScopeDisambiguationSyntax = true });

        // No enums, no functions. Adding functions will add back in associated enums, so to be truly empty, ensure no functions. 
        private PowerFxConfig EmptyEverything => PowerFxConfig.BuildWithEnumStore(new EnumStoreBuilder(), new TexlFunctionSet());

        // No extra enums, but standard functions (which will include some enums).
        private PowerFxConfig MinimalEnums => PowerFxConfig.BuildWithEnumStore(new EnumStoreBuilder().WithRequiredEnums(BuiltinFunctionsCore._library));

        /// <summary>
        /// Compares expected suggestions with suggestions made by PFx Intellisense for a given
        /// <see cref="expression"/> and cursor position. The cursor position is determined by the index of
        /// the | character in <see cref="expression"/>, which will be removed but the test is run. Note that
        /// the use of the `|` char is for this reason disallowed from test cases except to indicate cursor
        /// position. Note also that these test suggestion order as well as contents.
        /// </summary>
        /// <param name="expression">
        /// Expression on which intellisense will be run.
        /// </param>
        /// <param name="expectedSuggestions">
        /// A list of arguments that will be compared with the names of the output of
        /// <see cref="Workspace.Suggest"/> in order.
        /// </param>
        [Theory]

        // CommentNodeSuggestionHandler
        [InlineData("// No| intellisense inside comment")]

        // RecordNodeSuggestionHandler
        [InlineData("{} |", "As", "exactin", "in")]
        [InlineData("{'complex record':{column:{}}} |", "As", "exactin", "in")]

        // DottedNameNodeSuggestionHandler
        [InlineData("{a:{},b:{},c:{}}.|", "a", "b", "c")]
        [InlineData("$\"Hello { First(Table({a:{},b:{},c:{}})).| } World\"", "a", "b", "c")]
        [InlineData("$\"Hello { First(Table({a:{},b:{},c:{}})).|   \"", "a", "b", "c")]
        [InlineData("$\"Hello { {a:{},b:{},c:{}}.|  \"", "a", "b", "c")]
        [InlineData("First([$\"{ {a:1,b:2,c:3}.|", "a", "b", "c")]
        [InlineData("$\"Hello {\"|")]
        [InlineData("$\"Hello {}\"|")]
        [InlineData("$\"Hello {|}\"")]
        [InlineData("$\"{ {a:{},b:{},c:{}}.}{|}\"")]
        [InlineData("$\"{ {a:{},b:{},c:{}}}{|}\"")]
        [InlineData("$ |")]
        [InlineData("$\"foo {|")]
        [InlineData("$\"foo { {a:| } } \"")]
        [InlineData("{abc:{},ab:{},a:{}}.|ab", "ab", "a", "abc")]
        [InlineData("{abc:{},ab:{},a:{}}.ab|", "ab", "abc")]
        [InlineData("{abc:{},ab:{},a:{}}.ab|c", "abc", "ab")]
        [InlineData("{abc:{},ab:{},a:{}}.abc|", "abc")]
        [InlineData("{abc:{},ab:{},a:{}}.| abc", "a", "ab", "abc")]
        [InlineData("{abc:{},ab:{},a:{}}.abc |", "As", "exactin", "in")]
        [InlineData("{az:{},z:{}}.|", "az", "z")]
        [InlineData("{az:{},z:{}}.z|", "z", "az")]

        // We don't recommend anything for one column tables only if the one column table is referenced
        // by the following dotted name access.
        [InlineData("[\"test\"].Value.| ")]
        [InlineData("[{test:\",test\"}].test.| ")]

        // We do, however, if the one column table is a literal.
        [InlineData("[\"test\"].| ")]
        [InlineData("Calendar.|", "MonthsLong", "MonthsShort", "WeekdaysLong", "WeekdaysShort")]
        [InlineData("Calendar.Months|", "MonthsLong", "MonthsShort")]
        [InlineData("Color.AliceBl|", "AliceBlue")]
        [InlineData("Color.Pale|", "PaleGoldenRod", "PaleGreen", "PaleTurquoise", "PaleVioletRed")]

        // CallNodeSuggestionHandler
        [InlineData("ForAll|([1],Value)", "ForAll")]
        [InlineData("at|(", "Atan", "Atan2", "Concat", "Concatenate", "Date", "DateAdd", "DateDiff", "DateTime", "DateTimeValue", "DateValue", "EDate", "Patch")]
        [InlineData("Atan |(")]
        [InlineData("Clock.A|(", "Clock.AmPm", "Clock.AmPmShort")]
        [InlineData("ForAll([\"test\"],EndsWith(|))", "Value")]
        [InlineData("ForAll([1],Value) |", "As", "exactin", "in")]

        // BoolLitNodeSuggestionHandler
        [InlineData("true|", "true")]
        [InlineData("tru|e", "true", "Trunc")]
        [InlineData("false |", "-", "&", "&&", "*", "/", "^", "||", "+", "<", "<=", "<>", "=", ">", ">=", "And", "As", "exactin", "in", "Or")]

        // BinaryOpNodeSuggestionHandler
        [InlineData("1 +|", "+")]
        [InlineData("1 |+", "-", "&", "&&", "*", "/", "^", "||", "+", "<", "<=", "<>", "=", ">", ">=", "And", "As", "exactin", "in", "Or")]
        [InlineData("\"1\" in|", "in", "exactin")]
        [InlineData("true &|", "&", "&&")]

        // UnaryOpNodeSuggestionHandler
        [InlineData("Not| false", "Not", "ErrorKind.FileNotFound", "ErrorKind.NotApplicable", "ErrorKind.NotFound", "ErrorKind.NotSupported")]
        [InlineData("| Not false")]
        [InlineData("Not |")]

        // StrInterpSuggestionHandler
        [InlineData("With( {Apples:3}, $\"We have {appl|", "Apples", "ErrorKind.NotApplicable")]
        [InlineData("With( {Apples:3}, $\"We have {appl|} apples.", "Apples", "ErrorKind.NotApplicable")]
        [InlineData("$\"This is a randomly generated number: {rand|", "Rand", "RandBetween")]

        // StrNumLitNodeSuggestionHandler
        [InlineData("1 |", "-", "&", "&&", "*", "/", "^", "||", "+", "<", "<=", "<>", "=", ">", ">=", "And", "As", "exactin", "in", "Or")]
        [InlineData("1|0")]
        [InlineData("\"Clock|\"")]

        // FirstNameNodeSuggestionHandler
        [InlineData("Tru|", "true", "Trunc")] // Though it recommends only a boolean, the suggestions are still provided by the first name handler
        [InlineData("[@In|]", "ErrorKind", "JoinType")]

        // FunctionRecordNameSuggestionHandler
        [InlineData("Error({Kin|d:0})")]
        [InlineData("Error({|Kind:0, Test:\"\"})")]

        // ErrorNodeSuggestionHandler
        [InlineData("ForAll([0],`|", "ThisRecord", "Value")]
        [InlineData("ForAll(-],|", "ThisRecord")]
        [InlineData("ForAll()~|")]
        [InlineData("With( {Apples:3}, $\"We have {Apples} apples|")]

        // BlankNodeSuggestionHandler
        [InlineData("|")]

        // AddSuggestionsForEnums
        [InlineData("Monday|", "StartOfWeek.Monday", "StartOfWeek.MondayZero")]
        [InlineData("Value(Missing|", "ErrorKind.MissingRequired")]
        [InlineData("ErrorKind.Inv|", "InvalidArgument", "InvalidFunctionUsage", "InvalidJSON")]
        [InlineData("Quota|", "ErrorKind.QuotaExceeded")]
        [InlineData("DateTimeFormat.h|", "ShortDate", "ShortTime", "ShortTime24", "ShortDateTime", "ShortDateTime24")]
        [InlineData("SortOrder|", "SortOrder", "SortOrder.Ascending", "SortOrder.Descending")]
        [InlineData("$\"Hello {SortOrder|} World!\"", "SortOrder", "SortOrder.Ascending", "SortOrder.Descending")]

        [InlineData("Table({F1:1}).|")]
        [InlineData("Table({F1:1},{F1:2}).|")]
        [InlineData("Table({F1:1, F2:2},{F2:1}).|")]
        [InlineData("[1,2,3].|")]
        [InlineData("With({testVar: \"testStr\"}, InvalidFunc(StartsWith(test|", "testVar")]

        // Suggests keywords and reserved words with indentifier escape.
        [InlineData("With({'Children':1}, ThisRecord.|", "'Children'")]
        [InlineData("With({'true':1}, ThisRecord.|", "'true'")]
        [InlineData("With({'Children':1,'Child':1, Cuscuz:1}, ThisRecord.C|", "'Child'", "'Children'", "Cuscuz")]
        public void TestSuggest(string expression, params string[] expectedSuggestions)
        {
            // Note that the expression string needs to have balanced quotes or we hit a bug in NUnit running the tests:
            //   https://github.com/nunit/nunit3-vs-adapter/issues/691
            var config = Default;
            var actualSuggestions = SuggestStrings(expression, config);
            Assert.Equal(expectedSuggestions.OrderBy(x => x), actualSuggestions.OrderBy(x => x));

            actualSuggestions = SuggestStrings(expression, config);
            Assert.Equal(expectedSuggestions.OrderBy(x => x), actualSuggestions.OrderBy(x => x));
        }

        // Suggests multiple scopes for functions that creates multiple scopes.
        [Theory]
        [InlineData("Join(Table({a:1,b:1}),Table({a:1,c:2,d:2}),|", "LeftRecord", "RightRecord")]
        [InlineData("Join(Table({a:1,b:1}) As t1,Table({a:1,c:2,d:2}) As t2,|", "t1", "t2")]
        [InlineData("Join(Table({a:1,b:1}),Table({a:1,c:2,d:2}),LeftRecord.|", "a", "b")]
        [InlineData("Join(Table({a:1,b:1}),Table({a:1,c:2,d:2}),LeftRecord.a = RightRecord.|", "a", "c", "d")]
        [InlineData("Join(Table({a:1,b:1}),Table({a:1,c:2,d:2}),LeftRecord.a = RightRecord.a,|", "JoinType.Full", "JoinType.Inner", "JoinType.Left", "JoinType.Right")]
        [InlineData("Join(Table({a:1,b:1}),Table({a:1,c:2,d:2}),LeftRecord.a = RightRecord.a, JoinType.Inner,|", "LeftRecord", "RightRecord")]
        [InlineData("Join(Table({a:1,b:1}),Table({a:1,c:2,d:2}),LeftRecord.a = RightRecord.a, JoinType.Inner,LeftRecord.|", "a", "b")]
        [InlineData("Join(Table({a:1,b:1}),Table({a:1,c:2,d:2}),LeftRecord.a = RightRecord.a, JoinType.Inner,RightRecord.|", "a", "c", "d")]
        public void TestSuggestMultipleScopes(string expression, params string[] expectedSuggestions)
        {
            var config = Default;
            config.SymbolTable.AddFunction(new JoinFunction());

            var actualSuggestions = SuggestStrings(expression, config);
            Assert.Equal(expectedSuggestions.OrderBy(x => x), actualSuggestions.OrderBy(x => x));
        }

        // Making sure that functions with multiple scopes wont throw an out of range exception.
        [Theory]
        [InlineData("Join(Sequence(3), ForAll(Sequence(3,1,2), { Value : ThisRecord.Value, Value2 : ThisRecord.Value * ThisRecord.Value}), LeftRecord.Value = RightRecord.Value, JoinType.Left, RightRecord.Value2 As V2)")]
        public void MultipleScopesExceptionTest(string expression)
        {
            var config = new PowerFxConfig();
            config.SymbolTable.AddFunction(new JoinFunction());

            // Test if at any point in the expression we going to get a out of range exception.
            for (int i = 1; i < expression.Length; i++)
            {
#pragma warning disable CA1845 // Use span-based 'string.Concat'
                var subExpr = expression.Substring(0, i) + "|";
#pragma warning restore CA1845 // Use span-based 'string.Concat'

                try
                {
                    SuggestStrings(subExpr, config);
                }
                catch (Exception ex)
                {
                    Assert.Fail(ex.Message);
                }
            }

            Assert.True(true);
        }

        /// <summary>
        /// In cases for Intellisense with an empty enum store and no function list.
        /// </summary>
        [Theory]
        [InlineData("Color.AliceBl|")]
        [InlineData("Color.Pale|")]
        [InlineData("Edit|")]
        [InlineData("DisplayMode.E|")]
        [InlineData("Disabled|")]
        [InlineData("DisplayMode.D|")]
        [InlineData("DisplayMode|")]
        public void TestSuggestEmptyEnumList(string expression, params string[] expectedSuggestions)
        {
            var config = EmptyEverything;
            var actualSuggestions = SuggestStrings(expression, config);
            Assert.Equal(expectedSuggestions, actualSuggestions);

            actualSuggestions = SuggestStrings(expression, config);
            Assert.Equal(expectedSuggestions, actualSuggestions);
        }

        /// <summary>
        /// In cases for Intellisense with an empty enum store, but still builtin functions. 
        /// </summary>
        [Theory]
        [InlineData("Calendar.|", "MonthsLong", "MonthsShort", "WeekdaysLong", "WeekdaysShort")]
        [InlineData("Calendar.Months|", "MonthsLong", "MonthsShort")]
        public void TestSuggestEmptyAll(string expression, params string[] expectedSuggestions)
        {
            var config = MinimalEnums;
            var actualSuggestions = SuggestStrings(expression, config);
            Assert.Equal(expectedSuggestions, actualSuggestions);

            actualSuggestions = SuggestStrings(expression, config);
            Assert.Equal(expectedSuggestions, actualSuggestions);
        }

        [Fact]
        public void TestSuggestEscapedEnumName()
        {
            var enumStoreBuilder = new EnumStoreBuilder();
            enumStoreBuilder.TestOnly_WithCustomEnum(new EnumSymbol(
                new DName("Name That.Requires!escaping"),
                DType.Number,
                new Dictionary<string, object>()
                {
                    { "Field1", 1 },
                    { "Field2", 2 },
                }));
            var config = PowerFxConfig.BuildWithEnumStore(enumStoreBuilder);

            var result = SuggestStrings("Fiel|", config);
            Assert.Equal(2, result.Length);
            Assert.Contains("'Name That.Requires!escaping'.Field1", result);
            Assert.Contains("'Name That.Requires!escaping'.Field2", result);
        }

        [Theory]
        [InlineData("SortByColumns(|", 3, "The table to sort.", "SortByColumns(source, column, ...)")]
        [InlineData("SortByColumns(tbl1,|", 3, "A unique column name.", "SortByColumns(source, column, ...)")]
        [InlineData("SortByColumns(tbl1,col1,|", 2, "SortOrder.Ascending or SortOrder.Descending", "SortByColumns(source, column, order, ...)")]
        [InlineData("SortByColumns(tbl1,col1,SortOrder.Ascending,|", 2, "A unique column name.", "SortByColumns(source, column, order, column, ...)")]
        [InlineData("IfError(1|", 1, "Value that is returned if it is not an error.", "IfError(value, fallback, ...)")]
        [InlineData("IfError(1,2|", 1, "Value that is returned if the previous argument is an error.", "IfError(value, fallback, ...)")]
        [InlineData("IfError(1,2,3|", 1, "Value that is returned if it is not an error.", "IfError(value, fallback, value, ...)")]
        [InlineData("IfError(1,2,3,4|", 1, "Value that is returned if the previous argument is an error.", "IfError(value, fallback, value, fallback, ...)")]
        [InlineData("IfError(1,2,3|,4", 1, "Value that is returned if it is not an error.", "IfError(value, fallback, value, fallback, ...)")]
        [InlineData("IfError(1,2,3,4,5|", 1, "Value that is returned if it is not an error.", "IfError(value, fallback, value, fallback, value, ...)")]
        [InlineData("IfError(1,2,3,4,5,6,7,8,9,0,1,2,3,4,5|", 1, "Value that is returned if it is not an error.", "IfError(value, fallback, value, fallback, ..., value, fallback, value, ...)")]
        public void TestIntellisenseFunctionParameterDescription(string expression, int expectedOverloadCount, string expectedDescription, string expectedDisplayText)
        {
            var context = "![tbl1:*[col1:n,col2:n]]";
            var result = Suggest(expression, Default, null, context);
            Assert.Equal(expectedOverloadCount, result.FunctionOverloads.Count());
            var currentOverload = result.FunctionOverloads.ToArray()[result.CurrentFunctionOverloadIndex];
            Assert.Equal(expectedDisplayText, currentOverload.DisplayText.Text);
            Assert.Equal(expectedDescription, currentOverload.FunctionParameterDescription);
        }

        [Theory]
        [InlineData("SortByColumns(|", "Tabela a ser classificada.", "Classifica 'source' (origem) com base na coluna, com a opção de especificar uma 'order' (ordem) de classificação.", "pt-BR")]
        [InlineData("First(|", "Table dont la première ligne sera retournée.", "Retourne la première ligne de « source ».", "fr-FR")]
        [InlineData("First(|", "A table whose first row will be returned.", "Returns the first row of 'source'.", "")] // Invariant culture
        [InlineData("If(|", "Condição que resulta em um valor booleano.", "Verifica se alguma das condições especificadas foi atendida e retorna o valor correspondente. Se nenhuma das condições for atendida, a função retornará o valor padrão especificado.", "pt-BR")]
        public void TestIntellisenseFunctionParameterDescriptionLocale(string expression, string expectedParameterDescription, string expectedDefinition, string locale)
        {
            var result = Suggest(expression, Default, CultureInfo.InvariantCulture, CultureInfo.CreateSpecificCulture(locale));

            var signature = result.SignatureHelp.Signatures[result.SignatureHelp.ActiveSignature];
            var overload = result.FunctionOverloads.ToArray()[result.CurrentFunctionOverloadIndex];

            Assert.Equal(expectedDefinition, signature.Documentation);
            Assert.Equal(expectedDefinition, overload.Definition);

            Assert.Equal(expectedParameterDescription, signature.Parameters[0].Documentation);
            Assert.Equal(expectedParameterDescription, overload.FunctionParameterDescription);
        }

        /// <summary>
        /// In cases for which Intellisense produces exceedingly numerous results, they should
        /// contain localized strings.
        /// </summary>
        [Fact]
        public void TestNonEmptyLocalizedSuggest()
        {
            var expression = "| ForAll([1],Value)";
            var config = Default;
            var suggestions = Suggest(expression, Default, null, CultureInfo.CreateSpecificCulture("pt-BR"));

            // Fetching arg1 (Abs function).
            var definition = suggestions.Suggestions.ToArray()[1].Definition;
            Assert.Equal("Retorna o valor absoluto de um número, sem o sinal.", definition);
        }

        [Theory]
        [InlineData("çava,comment,chat", "çava,chat,comment", "fr-FR")]
        [InlineData("azul,árvore,áurea", "árvore,áurea,azul", "pt-BR")]
        [InlineData("Choice,car", "car,Choice", "en-US")] // Case insensitive comparison
        public void TestIntellisenseSuggestionsSortOrder(string names, string expectedOrder, string culture)
        {
            var context = $"![{string.Join(",", names.Split(',').Select(s => $"variable{s}:n"))}]";
            var expectedSuggestions = expectedOrder.Split(',').Select(s => "variable" + s).ToArray();
            var config = PowerFxConfig.BuildWithEnumStore(new EnumStoreBuilder().WithDefaultEnums());

            var result = Suggest("variabl|", config, new CultureInfo(culture), context);
            var suggestions = result.Suggestions.ToList();

            Assert.Equal(expectedSuggestions.Length, suggestions.Count);

            for (var i = 0; i < expectedSuggestions.Length; i++)
            {
                Assert.Equal(expectedSuggestions[i], suggestions[i].DisplayText.Text);
            }
        }

        /// <summary>
        /// In cases for which Intellisense produces exceedingly numerous results it may be sufficient that
        /// they (the cases) be validated based on whether they return suggestions at all.
        /// </summary>
        [Theory]

        // CallNodeSuggestionHandler
        [InlineData("| ForAll([1],Value)")]

        // BoolLitNodeSuggestionHandler
        [InlineData("t|rue")]
        [InlineData("f|alse")]
        [InlineData("| false")]

        // UnaryOpNodeSuggestionHandler
        [InlineData("|Not false")]

        // FirstNameNodeSuggestionHandler
        [InlineData("| Test", "![Test: s]")]
        [InlineData("[@|]")]
        [InlineData("[@|")]
        public void TestNonEmptySuggest(string expression, string context = null)
        {
            var config = Default;
            var actualSuggestions = SuggestStrings(expression, Default, contextTypeString: context);
            Assert.True(actualSuggestions.Length > 0);

            actualSuggestions = SuggestStrings(expression, config);
            Assert.True(actualSuggestions.Length > 0);
        }

        [Theory]

        // FirstNameNodeSuggestionHandleractualSuggestions = IntellisenseResult
        [InlineData("Test|", "![Test1: s, Test2: n, Test3: h]", "Test1", "Test2", "Test3")]
        [InlineData("RecordName[|", "![RecordName: ![StringName: s, NumberName: n]]", "@NumberName", "@StringName")]
        [InlineData("RecordName[|", "![RecordName: ![]]")]
        [InlineData("Test |", "![Test: s]", "&", "&&", "*", "+", "-", "/", "<", "<=", "<>", "=", ">", ">=", "And", "As", "exactin", "in", "Or", "^", "||")]
        [InlineData("Filter(Table, Table[|", "![Table: *[Column: s]]", "@Column")]

        // ErrorNodeSuggestionHandler
        [InlineData("ForAll(Table,`|", "![Table: *[Column: s]]", "Column", "ThisRecord")]
        public void TestSuggestWithContext(string expression, string context, params string[] expectedSuggestions)
        {
            Assert.NotNull(context);

            var config = Default;
            var actualSuggestions = SuggestStrings(expression, config, contextTypeString: context);
            Assert.Equal(expectedSuggestions, actualSuggestions);

            actualSuggestions = SuggestStrings(expression, config, contextTypeString: context);
            Assert.Equal(expectedSuggestions, actualSuggestions);
        }

        [Theory]
        [InlineData("RecordName[|", "![RecordName: ![StringName: s, NumberName: n]]")]
        [InlineData("Filter(Table, Table[|", "![Table: *[Column: s]]")]
        public void TestSuggestWithContext_DisableRowScopeDisambiguationSyntax(string expression, string context, params string[] expectedSuggestions)
        {
            Assert.NotNull(context);

            var config = Default_DisableRowScopeDisambiguationSyntax;
            var actualSuggestions = SuggestStrings(expression, config, null, context);
            Assert.Equal(expectedSuggestions, actualSuggestions);

            actualSuggestions = SuggestStrings(expression, config, null, context);
            Assert.Equal(expectedSuggestions, actualSuggestions);
        }

        [Theory]
        [InlineData("So|", true, "SomeString")]
        [InlineData("Loop.Loop.Loop.So|", true, "SomeString")]
        [InlineData("Loop.|", true, "Loop", "Record", "SomeString", "TableLoop")]
        [InlineData("Record.|", false, "Foo")]
        [InlineData("Loop.Loop.Record.|", false, "Foo")]
        [InlineData("Filter(TableLoop, EndsWith(|", true, "SomeString")]
        [InlineData("Filter(TableLoop,|", true, "Loop", "Record", "SomeString", "TableLoop", "ThisRecord")]
        [InlineData("Loop.L|o", true, "Loop", "TableLoop")]
        public void TestSuggestLazyTypes(string expression, bool requiresExpansion, params string[] expectedSuggestions)
        {
            var lazyInstance = new LazyRecursiveRecordType();
            var config = PowerFxConfig.BuildWithEnumStore(                
                new EnumStoreBuilder(),
                new TexlFunctionSet(new[] { BuiltinFunctionsCore.EndsWith, BuiltinFunctionsCore.Filter, BuiltinFunctionsCore.Table }));
            var actualSuggestions = SuggestStrings(expression, config, null, lazyInstance);
            Assert.Equal(expectedSuggestions, actualSuggestions);

            // Intellisense requires iterating the field names for some operations
            Assert.Equal(requiresExpansion, lazyInstance.EnumerableIterated);

            actualSuggestions = SuggestStrings(expression, config, null, lazyInstance);
            Assert.Equal(expectedSuggestions, actualSuggestions);
        }

        [Theory]
        [InlineData("logica|")] // No suggestions for logical names
        [InlineData("displa|", "display1", "display2")] // display names
        public void TestSuggestDeferredSymbols(string expression, params string[] expectedSuggestions)
        {
            var map = new SingleSourceDisplayNameProvider(new Dictionary<DName, DName>
            {
                { new DName("logical1"), new DName("display1") },
                { new DName("logical2"), new DName("display2") }
            });

            var symTable = ReadOnlySymbolTable.NewFromDeferred(map, (disp, logical) =>
            {
                return FormulaType.Number;
            });

            var config = new PowerFxConfig();
            var actualSuggestions = SuggestStrings(expression, config, null, symTable);
            Assert.Equal(expectedSuggestions, actualSuggestions);
        }

        [Fact]
        public void SuggestDoesNotNeedErrors()
        {
            var engine = new Engine(new PowerFxConfig());
            var check = new CheckResult(engine);

            // Error, text isn't set
            Assert.Throws<InvalidOperationException>(() => engine.Suggest(check, 1));

            check.SetText("1+2");
            check.SetBindingInfo();
            var suggest = engine.Suggest(check, 1);
            Assert.NotNull(suggest);

            check.ApplyErrors();
            Assert.Empty(check.Errors);
        }

        [Theory]
        [InlineData("ThisRec|", "ThisRecord")]
        [InlineData("ThisRecord.|", "F1", "F2")]
        public void SuggestThisRecord(string expression, params string[] expected)
        {
            var recordType = RecordType.Empty()
                .Add("F1", FormulaType.Number)
                .Add("F2", FormulaType.String);

            var rowScopeSymbols = ReadOnlySymbolTable.NewFromRecord(recordType, allowThisRecord: true, allowMutable: true, debugName: $"RowScope");
            var config = new PowerFxConfig();
            var actualSuggestions = SuggestStrings(expression, config, null, rowScopeSymbols);
            Assert.Equal(expected, actualSuggestions);

            // No suggestion when allowThisRecord is false.
            rowScopeSymbols = ReadOnlySymbolTable.NewFromRecord(recordType, allowThisRecord: false, allowMutable: true, debugName: $"RowScope");
            actualSuggestions = SuggestStrings(expression, config, null, rowScopeSymbols);
            Assert.Empty(actualSuggestions);
        }

        [Theory]
        [InlineData("ThisRec|", "ThisRecord")]
        [InlineData("ThisRecord.|", "field1", "field2")]

        // Do not suggest field, unless explicitly ThisRecord is prepended.
        [InlineData("field|")]
        public void SuggestBlockImplicitThisRecord(string expression, params string[] expected)
        {
            var recordType = RecordType.Empty()
                .Add("field1", FormulaType.Number)
                .Add("field2", FormulaType.String);

            var rowScopeSymbols = ReadOnlySymbolTable.NewFromRecordWithoutImplicitThisRecord(recordType, allowMutable: true, debugName: $"RowScope");
            var config = new PowerFxConfig();
            var actualSuggestions = SuggestStrings(expression, config, null, rowScopeSymbols);
            Assert.Equal(expected, actualSuggestions);
        }

        [Theory]
        [InlineData("Use|", "Color.Chartreuse", "User")]

        //Should Not Suggest User here.
        [InlineData("Power(|")]
        public void SuggestUser(string expression, params string[] expected)
        {
            var config = SuggestTests.Default;

            config.SymbolTable.AddHostObject("User", RecordType.Empty(), (sp) => RecordValue.NewRecordFromFields());

            var actualSuggestions = SuggestStrings(expression, config, null);
            Assert.Equal(expected, actualSuggestions);
        }

        [Theory]
        [InlineData("{|", "output1:", "output2:")]

        [InlineData("{output1: 1, |", "output2:")]

        // We do not suggest nested type, as this can explode if type is DV.
        [InlineData("{output1: {|")]

        // output2 is tabletype, so don't show suggestion if current node is not a table.
        [InlineData("{output2: {|")]

        // We do not suggest nested type, as this can explode if type is DV.
        [InlineData("{output2: [{|")]

        // output2 is tabletype, so don't show suggestion if current node is not a table.
        [InlineData("{output1: {Arg1F1: 1, Arg1F2:\"test\"}, output2: {|")]

        // We do not suggest nested type, as this can explode if type is DV.
        [InlineData("{output1: {Arg1F1: 1, Arg1F2:\"test\"}, output2: [{|")]

        // We do not suggest nested type, as this can explode if type is DV.
        [InlineData("{output2: {Arg2Field1: 1, Arg2Field2:\"test\"}, output1: {|")]
        public void SuggestAggregateReturnType(string expression, params string[] expected)
        {
            (var exp, var cursorPosition) = Decode(expression);

            var config = SuggestTests.Default;

            var outputArg1 = RecordType.Empty()
                .Add("Arg1F1", FormulaType.Number)
                .Add("Arg1F2", FormulaType.String);

            var outputArg2 = RecordType.Empty()
                .Add("Arg2Field1", FormulaType.Number)
                .Add("Arg2Field2", FormulaType.String).ToTable();

            var expectedReturnType = RecordType.Empty()
                .Add("output1", outputArg1)
                .Add("output2", outputArg2);

            var engine = new Engine(config);
            var check = new CheckResult(engine)
                .SetText(exp)
                .SetBindingInfo()
                .SetExpectedReturnValue(expectedReturnType);

            var suggestion = engine.Suggest(check, cursorPosition).Suggestions.Select(suggestion => suggestion.DisplayText.Text);

            Assert.Equal(expected, suggestion);
        }

        [Theory]
        [InlineData("Filter(TableLoop,|")]
        [InlineData("Filter(Accounts,|")]
        public void LazyTypesStackOverflowTest(string expression)
        {
            var lazyInstance = new LazyRecursiveRecordType();
            var config = PowerFxConfig.BuildWithEnumStore(
                new EnumStoreBuilder(),
                new TexlFunctionSet(new[] { BuiltinFunctionsCore.Filter }));

            var suggestions = SuggestStrings(expression, config, null, lazyInstance);

            // Just check that the execution didn't stack overflow.
            Assert.True(suggestions.Any());
        }

        [Theory]
        [InlineData("ParseJSON(\"42\", Nu|", "Number")]
        [InlineData("AsType(ParseJSON(\"42\"), Da|", "Date", "DateTime", "DateTimeTZInd")]
        [InlineData("IsType(ParseJSON(\"42\"),|", "'My Type With Space'", "'Some \" DQuote'", "Boolean", "Date", "DateTime", "DateTimeTZInd", "Decimal", "Dynamic", "GUID", "Hyperlink", "MyNewType", "Number", "Text", "Time")]
        [InlineData("ParseJSON(\"42\", Voi|")]
        [InlineData("ParseJSON(\"42\", MyN|", "MyNewType")]
        [InlineData("ParseJSON(\"42\", Tim|", "DateTime", "DateTimeTZInd", "Time")]
        [InlineData("ParseJSON(\"42\", My|", "'My Type With Space'", "MyNewType")]
        [InlineData("ParseJSON(\"42\", So|", "'Some \" DQuote'")]
        public void TypeArgumentsTest(string expression, params string[] expected)
        {
            var symbolTable = SymbolTable.WithPrimitiveTypes();

            symbolTable.AddType(new DName("MyNewType"), FormulaType.String);
            symbolTable.AddType(new DName("My Type With Space"), FormulaType.String);
            symbolTable.AddType(new DName("Some \" DQuote"), FormulaType.String);
            symbolTable.AddFunctions(new TexlFunctionSet(BuiltinFunctionsCore.TestOnly_AllBuiltinFunctions.Where(f => f.HasTypeArgs).ToArray()));
            symbolTable.AddFunctions(new TexlFunctionSet(new[] { BuiltinFunctionsCore.ParseJSON }));

            var config = new PowerFxConfig();
            var actualSuggestions = SuggestStrings(expression, config, null, symbolTable);
            Assert.Equal(expected, actualSuggestions);
        }
    }
}
