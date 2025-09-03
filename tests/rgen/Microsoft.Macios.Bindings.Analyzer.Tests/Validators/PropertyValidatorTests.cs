// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma warning disable APL0003
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Macios.Bindings.Analyzer.Validators;
using Microsoft.Macios.Generator.Attributes;
using Microsoft.Macios.Generator.Availability;
using Microsoft.Macios.Generator.Context;
using Microsoft.Macios.Generator.DataModel;
using ObjCRuntime;
using Xunit;
using static Microsoft.Macios.Generator.Tests.TestDataFactory;

namespace Microsoft.Macios.Bindings.Analyzer.Tests.Validators;

public class PropertyValidatorTests {

	readonly RootContext context;

	public PropertyValidatorTests ()
	{
		// Create a dummy compilation to get a semantic model and RootContext
		var syntaxTree = CSharpSyntaxTree.ParseText ("namespace Test { }");
		var compilation = CSharpCompilation.Create (
			"TestAssembly",
			[syntaxTree],
			references: [],
			options: new CSharpCompilationOptions (OutputKind.DynamicallyLinkedLibrary)
		);
		var semanticModel = compilation.GetSemanticModel (syntaxTree);
		context = new RootContext (semanticModel);
	}

	Property CreateProperty (string name = "TestProperty",
		bool isPartial = true,
		string? propertySelector = "testProperty",
		ObjCBindings.Property propertyFlags = ObjCBindings.Property.Default,
		ArgumentSemantic argumentSemantic = ArgumentSemantic.None,
		string? getterSelector = null,
		string? setterSelector = null,
		bool hasGetter = true,
		bool hasSetter = true)
	{
		var modifiers = ImmutableArray.CreateBuilder<SyntaxToken> ();
		modifiers.Add (SyntaxFactory.Token (SyntaxKind.PublicKeyword));
		if (isPartial)
			modifiers.Add (SyntaxFactory.Token (SyntaxKind.PartialKeyword));

		var accessors = ImmutableArray.CreateBuilder<Accessor> ();

		if (hasGetter) {
			getterSelector ??= "sampleGetter";
			var getterExportData = new ExportData<ObjCBindings.Property> (getterSelector,
				argumentSemantic, ObjCBindings.Property.Default);

			accessors.Add (new Accessor (
				accessorKind: AccessorKind.Getter,
				symbolAvailability: new SymbolAvailability.Builder ().ToImmutable (),
				exportPropertyData: getterExportData,
				attributes: [],
				modifiers: []
			));
		}

		if (hasSetter) {
			setterSelector ??= "sampleSetter:";
			var setterExportData = new ExportData<ObjCBindings.Property> (setterSelector,
				argumentSemantic, ObjCBindings.Property.Default);

			accessors.Add (new Accessor (
				accessorKind: AccessorKind.Setter,
				symbolAvailability: new SymbolAvailability.Builder ().ToImmutable (),
				exportPropertyData: setterExportData,
				attributes: [],
				modifiers: []
			));
		}

		var availability = new SymbolAvailability.Builder ();
		availability.Add (new SupportedOSPlatformData ("ios"));
		availability.Add (new SupportedOSPlatformData ("tvos"));
		availability.Add (new SupportedOSPlatformData ("macos"));
		availability.Add (new SupportedOSPlatformData ("maccatalyst"));

		return new Property (
			name: name,
			returnType: ReturnTypeForString (),
			symbolAvailability: availability.ToImmutable (),
			attributes: [],
			modifiers: modifiers.ToImmutable (),
			accessors: accessors.ToImmutable ()
		) {
			ExportPropertyData = new ExportData<ObjCBindings.Property> (propertySelector,
				argumentSemantic, propertyFlags)
		};
	}

	[Theory]
	[InlineData (true, 0)] // Valid property: partial
	[InlineData (false, 1)] // Invalid: not partial
	public void ValidatePropertyModifiersTests (bool isPartial, int expectedDiagnosticsCount)
	{
		var validator = new PropertyValidator ();
		var property = CreateProperty (isPartial: isPartial);

		var result = validator.ValidateAll (property, context);

		var totalDiagnostics = result.Values.Sum (x => x.Count);
		Assert.Equal (expectedDiagnosticsCount, totalDiagnostics);
	}

	[Theory]
	[InlineData ("validSelector", true, 0)]
	[InlineData ("", false, 1)]
	[InlineData (null, false, 1)]
	[InlineData ("another_valid_selector", true, 0)]
	[InlineData ("valid123", true, 0)]
	public void PropertySelectorIsNotNullTests (string? selector, bool expectedResult, int expectedDiagnosticsCount)
	{
		var validator = new PropertyValidator ();
		var property = CreateProperty (propertySelector: selector);

		var result = validator.ValidateAll (property, context);
		Assert.Equal (expectedResult, result.Count == 0);

		var diagnostics = result.Values.SelectMany (x => x).ToList ();
		var selectorDiagnostics = diagnostics.Where (d => d.Id == "RBI0018").ToList ();

		Assert.Equal (expectedDiagnosticsCount, selectorDiagnostics.Count);
	}

	[Theory]
	[InlineData ("validSelector", true, 0)]
	[InlineData ("invalid selector", false, 1)]
	[InlineData ("selector\twith\ttab", false, 1)]
	[InlineData ("selector\nwith\nnewline", false, 1)]
	[InlineData ("selector with space", false, 1)]
	[InlineData ("valid_selector", true, 0)]
	[InlineData ("validSelector123", true, 0)]
	[InlineData (" leadingSpace", false, 1)]
	[InlineData ("trailingSpace ", false, 1)]
	public void PropertySelectorHasNoWhitespaceTests (string selector, bool expectedResult, int expectedDiagnosticsCount)
	{
		var validator = new PropertyValidator ();
		var property = CreateProperty (propertySelector: selector);

		var result = validator.ValidateAll (property, context);
		Assert.Equal (expectedResult, result.Count == 0);

		var diagnostics = result.Values.SelectMany (x => x).ToList ();
		var whitespaceDiagnostics = diagnostics.Where (d => d.Id == "RBI0019").ToList ();

		Assert.Equal (expectedDiagnosticsCount, whitespaceDiagnostics.Count);
	}

	[Theory]
	[InlineData ("validGetter", null, 0)] // Valid getter, no setter export
	[InlineData (null, "validSetter:", 0)] // No getter export, valid setter
	[InlineData ("validGetter", "validSetter:", 0)] // Both valid
	[InlineData ("", "validSetter:", 1)] // Invalid getter (empty)
	[InlineData ("validGetter", "", 1)] // Invalid setter (empty)
	[InlineData ("", "", 2)] // Both invalid
	public void AccessorSelectorValidationTests (string? getterSelector, string? setterSelector, int expectedDiagnosticsCount)
	{
		var validator = new PropertyValidator ();
		var property = CreateProperty (
			getterSelector: getterSelector,
			setterSelector: setterSelector
		);

		var result = validator.ValidateAll (property, context);

		var diagnostics = result.Values.SelectMany (x => x).ToList ();
		var selectorDiagnostics = diagnostics.Where (d => d.Id == "RBI0018").ToList ();

		Assert.Equal (expectedDiagnosticsCount, selectorDiagnostics.Count);
	}

	[Theory]
	[InlineData ("validGetter", "validSetter:", 0)] // Valid selectors
	[InlineData ("invalid getter", "validSetter:", 1)] // Invalid getter (whitespace)
	[InlineData ("validGetter", "invalid setter:", 1)] // Invalid setter (whitespace)
	[InlineData ("invalid getter", "invalid setter:", 2)] // Both invalid
	[InlineData ("getter\twith\ttab", "setter\nwith\nnewline:", 2)] // Both have different whitespace
	public void AccessorSelectorWhitespaceTests (string getterSelector, string setterSelector, int expectedDiagnosticsCount)
	{
		var validator = new PropertyValidator ();
		var property = CreateProperty (
			getterSelector: getterSelector,
			setterSelector: setterSelector
		);

		var result = validator.ValidateAll (property, context);

		var diagnostics = result.Values.SelectMany (x => x).ToList ();
		var whitespaceDiagnostics = diagnostics.Where (d => d.Id == "RBI0019").ToList ();

		Assert.Equal (expectedDiagnosticsCount, whitespaceDiagnostics.Count);
	}

	[Theory]
	[InlineData ("getPropertyValue", "setPropertyValue:", 0)] // Correct arg count: 0 for getter, 1 for setter
	[InlineData ("getPropertyValue:", "setPropertyValue", 2)] // Wrong arg count: 1 for getter, 0 for setter
	[InlineData ("getPropertyValue:withParam:", "setPropertyValue:", 1)] // Wrong getter arg count
	[InlineData ("getPropertyValue", "setPropertyValue:withExtraParam:", 1)] // Wrong setter arg count
	public void AccessorSelectorArgCountTests (string getterSelector, string setterSelector, int expectedDiagnosticsCount)
	{
		var validator = new PropertyValidator ();
		var property = CreateProperty (
			getterSelector: getterSelector,
			setterSelector: setterSelector
		);

		var result = validator.ValidateAll (property, context);

		var diagnostics = result.Values.SelectMany (x => x).ToList ();
		var argCountDiagnostics = diagnostics.Where (d => d.Id == "RBI0029").ToList ();

		Assert.Equal (expectedDiagnosticsCount, argCountDiagnostics.Count);
	}

	[Fact]
	public void ValidateValidPropertyTests ()
	{
		var validator = new PropertyValidator ();
		var property = CreateProperty (); // Default: partial, valid selectors

		var result = validator.ValidateAll (property, context);

		Assert.Empty (result);
	}

	[Theory]
	[InlineData (true, true, 0)] // Has getter and setter, both valid
	[InlineData (true, false, 0)] // Only getter, valid
	[InlineData (false, true, 0)] // Only setter, valid
	[InlineData (false, false, 0)] // No accessors, valid
	public void PropertyAccessorPresenceTests (bool hasGetter, bool hasSetter, int expectedDiagnosticsCount)
	{
		var validator = new PropertyValidator ();
		var property = CreateProperty (
			hasGetter: hasGetter,
			hasSetter: hasSetter
		);

		var result = validator.ValidateAll (property, context);

		var totalDiagnostics = result.Values.Sum (x => x.Count);
		Assert.Equal (expectedDiagnosticsCount, totalDiagnostics);
	}

	[Theory]
	[InlineData (false, "validGetter", null, 1)] // Not partial but has valid accessors
	[InlineData (false, "", "validSetter:", 2)] // Not partial and invalid getter
	[InlineData (false, "invalid getter", "invalid setter:", 3)] // Not partial and multiple issues
	public void CombinedValidationTests (bool isPartial, string? getterSelector, string? setterSelector, int expectedDiagnosticsCount)
	{
		var validator = new PropertyValidator ();
		var property = CreateProperty (
			isPartial: isPartial,
			getterSelector: getterSelector,
			setterSelector: setterSelector
		);

		var result = validator.ValidateAll (property, context);

		var totalDiagnostics = result.Values.Sum (x => x.Count);
		Assert.Equal (expectedDiagnosticsCount, totalDiagnostics);
	}
}
