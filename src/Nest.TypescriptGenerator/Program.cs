﻿using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using TypeLite;
using TypeLite.TsModels;
using Elasticsearch.Net;

namespace Nest.TypescriptGenerator
{
	public class Program
	{
		public static Dictionary<string, Type> RequestParameters { get; set; }
		private static MachineApiGenerator _scriptGenerator;
		private static readonly Regex InterfaceRegex = new Regex("(?-i)^I[A-Z].*$");
		public static Regex RemoveGeneric { get; } = new Regex(@"^(.+)(?:\`.+)$");

		private static string NestFolder = @"..\..\src\Nest";

		private static readonly string[] SkipFolders = { "_Generated", "Debug", "Release" };
		public static IEnumerable<References> InputFiles() =>
			from f in Directory.GetFiles(NestFolder, $"*.cs", SearchOption.AllDirectories)
			let dir = new DirectoryInfo(f)
			where dir?.Parent != null && !SkipFolders.Contains(dir.Parent.Name)
			select new References(new FileInfo(f));

		public static void Main(string[] args)
		{

			var currentDirectory = new DirectoryInfo(Directory.GetCurrentDirectory());

			var references = InputFiles()
				.SelectMany(r => r.Declarations, (r, s) => new { r, s });

			var dictionary = new Dictionary<string, string>();
			foreach(var r in references)
			{
				if (dictionary.ContainsKey(r.s) && (r.s.EndsWith("Request") || r.s.EndsWith("Descriptor")) )
				{
				}
				else if (dictionary.ContainsKey(r.s))
				{
				}
				else
				{
					dictionary.Add(r.s, r.r.Namespace);
				}
			}

			var isBadClassRe = new Regex(@"(Descriptor|Attribute)(?:Base)?(?:\`.+$|$)");

			var nestAssembly = typeof(IRequest<>).Assembly;
			var lowLevelAssembly = typeof(IElasticLowLevelClient).Assembly;

			var exposeInterfaces = new[] 
			{
				typeof(IRequest), typeof(IResponse), typeof(ICharFilter), typeof(ITokenFilter), typeof(IAnalyzer), typeof(ITokenizer),
				typeof(IIndicesModuleSettings), typeof(IProperty)
			};

			var nestInterfaces = nestAssembly
				.GetTypes()
				.Where(t => exposeInterfaces.Any(i=>i.IsAssignableFrom(t)))
				.Where(t => t.IsClass && !isBadClassRe.IsMatch(t.Name))
				.ToArray();

			RequestParameters = lowLevelAssembly
				.GetTypes()
				.Where(t => t.IsClass && t.Name.EndsWith("RequestParameters"))
				.ToDictionary(t=>t.Name.Replace("Parameters", ""));

			_scriptGenerator = new MachineApiGenerator(dictionary);

			var typeScriptFluent = TypeScript.Definitions(_scriptGenerator)
				.WithTypeFormatter(FormatType)
				.WithMemberFormatter(FormatMember)
				.WithMemberTypeFormatter(FormatMemberType)
				.WithVisibility((@class, name) => false)
				.AsConstEnums(false)
				.WithModuleNameFormatter(module => string.Empty);

			typeScriptFluent.For(typeof (Map<,>));
			typeScriptFluent.For<DateInterval>();
			var definitions = nestInterfaces.Aggregate(typeScriptFluent, (def, t) => def.For(t));

			File.WriteAllText("typedefinitions.ts", definitions.Generate());
		}

		private static string FormatMemberType(TsProperty tsProperty, string memberTypeName)
		{
			var asCollection = tsProperty.PropertyType as TsCollection;
			var isCollection = asCollection != null;

			return memberTypeName.StartsWith("Map<")
				? memberTypeName
				: memberTypeName + (isCollection ? string.Concat(Enumerable.Repeat("[]", asCollection.Dimension)) : "");
		}

		private static string FormatMember(TsProperty property)
		{
			var declaringType = property.MemberInfo.DeclaringType;
			var propertyName = property.MemberInfo.Name;

			if (declaringType == null) return propertyName;
			var iface = declaringType.GetInterfaces().FirstOrDefault(ii => ii.Name == "I" + declaringType.Name);
			var ifaceProperty = iface?.GetProperty(propertyName);

			var jsonPropertyAttribute = ifaceProperty?.GetCustomAttribute<JsonPropertyAttribute>() ??
			                            property.MemberInfo.GetCustomAttribute<JsonPropertyAttribute>();

			if (!string.IsNullOrWhiteSpace(jsonPropertyAttribute?.PropertyName))
				propertyName = jsonPropertyAttribute.PropertyName;

			return propertyName;
		}

		private static Regex SnakeCaseRe = new Regex("(?<=.)([A-Z])");
		private static string SnakeCase(string token) => SnakeCaseRe.Replace(token, "_$0").ToLowerInvariant();

		private static string FormatType(TsType type, ITsTypeFormatter formatter)
		{
			return GenerateTypeName(type);
		}

		private static string GenerateTypeName(TsType type)
		{
			var tsClass = (TsClass)type;

			var name = _scriptGenerator.TypeRenames.ContainsKey(tsClass.Name) 
				? _scriptGenerator.TypeRenames[tsClass.Name] 
				: tsClass.Name;

			if (InterfaceRegex.IsMatch(name)) name = name.Substring(1);

			name = SnakeCase(name);
			if (!tsClass.GenericArguments.Any()) return name;

			return name + "<" + string.Join(", ", tsClass.GenericArguments.Select(WriteArrayIfCollection)) + ">";
		}

		private static string WriteArrayIfCollection(TsType a)
		{
			var fullyQualifiedTypeName = _scriptGenerator.GetFullyQualifiedTypeName(a);

			return a is TsCollection && !fullyQualifiedTypeName.StartsWith("Map<") 
				? fullyQualifiedTypeName + "[]" 
				: fullyQualifiedTypeName;
		}
	}
}