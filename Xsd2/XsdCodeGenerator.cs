﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Schema;
using System.Xml.Serialization;
using System.CodeDom;
using System.CodeDom.Compiler;
using System.ComponentModel;

using Microsoft.CSharp;
using Microsoft.VisualBasic;

using Xsd2.Capitalizers;

namespace Xsd2
{
    public class XsdCodeGenerator
    {
        public XsdCodeGeneratorOptions Options { get; set; }
        public Action<CodeNamespace, XmlSchema> OnValidateGeneratedCode { get; set; }

        XmlSchemas xsds = new XmlSchemas();
        HashSet<XmlSchema> importedSchemas = new HashSet<XmlSchema>();

        public void Generate(IList<String> schemas, TextWriter output)
        {
            if (Options == null)
            {
                Options = new XsdCodeGeneratorOptions
                {
                    UseLists = true,
                    PropertyNameCapitalizer = new FirstCharacterCapitalizer(),
                    OutputNamespace = "Xsd2",
                    UseNullableTypes = true,
                    AttributesToRemove =
                    {
                        "System.Diagnostics.DebuggerStepThroughAttribute"
                    }
                };
            }

            if (Options.Imports != null)
            {
                foreach (var import in Options.Imports)
                {
                    if (File.Exists(import))
                    {
                        ImportImportedSchema(import);
                    }
                    else if (Directory.Exists(import))
                    {
                        foreach (var file in Directory.GetFiles("*.xsd"))
                            ImportImportedSchema(file);
                    }
                    else
                    {
                        throw new InvalidOperationException(String.Format("Import '{0}' is not a file nor a directory.", import));
                    }
                }
            }

            var inputs = new List<XmlSchema>();

            foreach (var path in schemas)
            {
                using (var r = File.OpenText(path))
                {
                    XmlSchema xsd = XmlSchema.Read(r, null);
                    xsds.Add(xsd);
                    inputs.Add(xsd);
                }
            }

            xsds.Compile(null, true);

            XmlSchemaImporter schemaImporter = new XmlSchemaImporter(xsds);


            // create the codedom
            CodeNamespace codeNamespace = new CodeNamespace(Options.OutputNamespace);
            XmlCodeExporter codeExporter = new XmlCodeExporter(codeNamespace);

            List<XmlTypeMapping> maps = new List<XmlTypeMapping>();
            foreach (var xsd in inputs)
                foreach (XmlSchemaElement schemaElement in xsd.Elements.Values)
                {
                    if (!ElementBelongsToImportedSchema(schemaElement) && !ExcludeName(schemaElement))
                        maps.Add(schemaImporter.ImportTypeMapping(schemaElement.QualifiedName));
                }


            foreach (var xsd in inputs)
                foreach (XmlSchemaComplexType schemaElement in xsd.Items.OfType<XmlSchemaComplexType>())
                {
                    if (ExcludeName(schemaElement))
                        continue;

                    maps.Add(schemaImporter.ImportSchemaType(schemaElement.QualifiedName));
                }

            foreach (var xsd in inputs)
                foreach (XmlSchemaSimpleType schemaElement in xsd.Items.OfType<XmlSchemaSimpleType>())
                {
                    if (ExcludeName(schemaElement))
                        continue;

                    maps.Add(schemaImporter.ImportSchemaType(schemaElement.QualifiedName));
                }

            foreach (XmlTypeMapping map in maps)
            {
                codeExporter.ExportTypeMapping(map);
            }

            ImproveCodeDom(codeNamespace);

            var usageTree = new UsageTree(codeNamespace);

            RemoveElements(codeNamespace, inputs, usageTree);

            if (OnValidateGeneratedCode != null)
                foreach (var xsd in inputs)
                    OnValidateGeneratedCode(codeNamespace, xsd);

            // Check for invalid characters in identifiers
            CodeGenerator.ValidateIdentifiers(codeNamespace);

            if (Options.WriteFileHeader)
            {
                // output the header
                string lineCommentCharacter;
                switch (Options.Language)
                {
                    case XsdCodeGeneratorOutputLanguage.VB:
                        lineCommentCharacter = "'";
                        break;
                    default:
                        lineCommentCharacter = "//";
                        break;
                }

                output.WriteLine("{0}------------------------------------------------------------------------------", lineCommentCharacter);
                output.WriteLine("{0} <auto-generated>", lineCommentCharacter);
                output.WriteLine("{0}     This code has been generated by a tool.", lineCommentCharacter);
                output.WriteLine("{0} </auto-generated>", lineCommentCharacter);
                output.WriteLine("{0}------------------------------------------------------------------------------", lineCommentCharacter);
                output.WriteLine();
            }

            // output the C# code
            CodeDomProvider codeProvider;
            switch (Options.Language)
            {
                case XsdCodeGeneratorOutputLanguage.VB:
                    codeProvider = new VBCodeProvider();
                    break;
                default:
                    codeProvider = new CSharpCodeProvider();
                    break;
            }

            codeProvider.GenerateCodeFromNamespace(codeNamespace, output, new CodeGeneratorOptions());
        }

        private void ImportImportedSchema(string schemaFilePath)
        {
            using (var s = File.OpenRead(schemaFilePath))
            {
                var importedSchema = XmlSchema.Read(s, null);
                xsds.Add(importedSchema);
                importedSchemas.Add(importedSchema);
            }
        }

        private bool ElementBelongsToImportedSchema(XmlSchemaElement element)
        {
            var node = element.Parent;
            while (node != null)
            {
                if (node is XmlSchema)
                {
                    var schema = (XmlSchema)node;
                    return importedSchemas.Contains(schema);
                }
                else
                    node = node.Parent;
            }
            return false;
        }

        private bool ExcludeName(XmlSchemaType type)
        {
            return ExcludeName(type.QualifiedName.ToString());
        }

        private bool ExcludeName(XmlSchemaElement type)
        {
            return ExcludeName(type.QualifiedName.ToString());
        }

        private bool ExcludeName(string qualifiedName)
        {
            if (Options.ExcludeXmlTypes == null)
                return false;

            return Options.ExcludeXmlTypes.Contains(qualifiedName);
        }

        /// <summary>
        /// Shamelessly taken from Xsd2Code project
        /// </summary>
        private bool ContainsTypeName(XmlSchema schema, CodeTypeDeclaration type)
        {
            //TODO: Does not work for combined anonymous types
            //fallback: Check if the namespace attribute of the type equals the namespace of the file.
            //first, find the XmlType attribute.
            var ns = ExtractNamespace(type);
            if (ns != null && ns != schema.TargetNamespace)
                return false;

            if (!Options.ExcludeImportedTypesByNameAndNamespace)
                return true;

            string typeName = type.GetXmlName();

            foreach (var item in schema.Items)
            {
                var complexItem = item as XmlSchemaComplexType;
                if (complexItem != null)
                {
                    if (complexItem.Name == typeName)
                    {
                        return true;
                    }
                }

                var simpleItem = item as XmlSchemaSimpleType;
                if (simpleItem != null)
                {
                    if (simpleItem.Name == typeName)
                    {
                        return true;
                    }
                }


                var elementItem = item as XmlSchemaElement;
                if (elementItem != null)
                {
                    if (elementItem.Name == typeName)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private String ExtractNamespace(CodeTypeDeclaration type)
        {
            foreach (CodeAttributeDeclaration attribute in type.CustomAttributes)
            {
                if (attribute.Name == "System.Xml.Serialization.XmlTypeAttribute")
                {
                    foreach (CodeAttributeArgument argument in attribute.Arguments)
                    {
                        if (argument.Name == "Namespace")
                        {
                            return (string)((CodePrimitiveExpression)argument.Value).Value;
                        }
                    }
                }
            }

            return null;
        }

        private void RemoveElements(CodeNamespace codeNamespace, IReadOnlyCollection<XmlSchema> inputs, UsageTree usageTree)
        {
            // Remove attributes
            foreach (CodeTypeDeclaration codeType in codeNamespace.Types)
            {
                var attributesToRemove = new HashSet<CodeAttributeDeclaration>();
                foreach (CodeAttributeDeclaration att in codeType.CustomAttributes)
                {
                    if (Options.AttributesToRemove.Contains(att.Name))
                    {
                        attributesToRemove.Add(att);
                    }
                    else
                    {
                        switch (att.Name)
                        {
                            case "System.Xml.Serialization.XmlRootAttribute":
                                var nullableArgument = att.Arguments.Cast<CodeAttributeArgument>().FirstOrDefault(x => x.Name == "IsNullable");
                                if (codeType.IsEnum || (nullableArgument != null && (bool)((CodePrimitiveExpression)nullableArgument.Value).Value))
                                {
                                    // Remove nullable root attribute
                                    attributesToRemove.Add(att);
                                }
                                break;
                        }
                    }
                }

                foreach (var att in attributesToRemove)
                    codeType.CustomAttributes.Remove(att);
            }

            // Remove types
            if (Options.ExcludeImportedTypes && Options.Imports != null && Options.Imports.Count > 0)
            {
                var removedTypes = codeNamespace.Types.Cast<CodeTypeDeclaration>().ToList();
                var anonymousTypes = new List<CodeTypeDeclaration>();

                while (removedTypes.RemoveAll(codeType =>
                {
                    if ((codeType.IsAnonymousType() && !codeType.IsRootType()) || codeType.IsIncludeInSchemaFalse())
                    {
                        if (!usageTree.LookupUsages(codeType).All(x => removedTypes.Contains(x.Type)))
                            return true;
                    }
                    else if (inputs.Any(schema => ContainsTypeName(schema, codeType)))
                    {
                        return true;
                    }

                    return false;
                }) > 0);

                // Remove types
                foreach (var rt in removedTypes)
                    codeNamespace.Types.Remove(rt);
            }
        }

        private void ImproveCodeDom(CodeNamespace codeNamespace)
        {
            var nonElementAttributes = new HashSet<string>(new[]
            {
                "System.Xml.Serialization.XmlAttributeAttribute",
                "System.Xml.Serialization.XmlIgnoreAttribute",
                "System.Xml.Serialization.XmlTextAttribute",
            });

            var nullValue = new CodePrimitiveExpression();

            codeNamespace.Imports.Add(new CodeNamespaceImport("System"));
            codeNamespace.Imports.Add(new CodeNamespaceImport("System.Collections.Generic"));

            if (Options.UsingNamespaces != null)
                foreach (var ns in Options.UsingNamespaces)
                    codeNamespace.Imports.Add(new CodeNamespaceImport(ns));

            var neverBrowsableAttribute = new CodeAttributeDeclaration("System.ComponentModel.EditorBrowsable",
                new CodeAttributeArgument(new CodeFieldReferenceExpression(new CodeTypeReferenceExpression("System.ComponentModel.EditorBrowsableState"), "Never")));

            var changedTypeNames = new Dictionary<string, string>();
            var newTypeNames = new HashSet<string>();

            if (Options.UseXLinq)
            {
                changedTypeNames.Add("System.Xml.XmlNode", "System.Xml.Linq.XNode");
                changedTypeNames.Add("System.Xml.XmlElement", "System.Xml.Linq.XElement");
                changedTypeNames.Add("System.Xml.XmlAttribute", "System.Xml.Linq.XAttribute");
            }

            foreach (CodeTypeDeclaration codeType in codeNamespace.Types)
            {
                if (Options.TypeNameCapitalizer != null)
                {
                    var newName = Options.TypeNameCapitalizer.Capitalize(codeNamespace, codeType);
                    if (newName != codeType.Name)
                    {
                        SetAttributeOriginalName(codeType, codeType.GetOriginalName(), "System.Xml.Serialization.XmlTypeAttribute");
                        var newNameToAdd = newName;
                        var index = 0;
                        while (!newTypeNames.Add(newNameToAdd))
                        {
                            index += 1;
                            newNameToAdd = string.Format("{0}{1}", newName, index);
                        }
                        changedTypeNames.Add(codeType.Name, newNameToAdd);
                        codeType.Name = newNameToAdd;
                    }
                }

                var members = new Dictionary<string, CodeTypeMember>();
                foreach (CodeTypeMember member in codeType.Members)
                    members[member.Name] = member;

                if (Options.EnableDataBinding && codeType.IsClass && codeType.BaseTypes.Count == 0)
                {
                    codeType.BaseTypes.Add(typeof(object));
                    codeType.BaseTypes.Add(typeof(INotifyPropertyChanged));

                    codeType.Members.Add(new CodeMemberEvent()
                    {
                        Name = "PropertyChanged",
                        ImplementationTypes = { typeof(INotifyPropertyChanged) },
                        Attributes = MemberAttributes.Public,
                        Type = new CodeTypeReference(typeof(PropertyChangedEventHandler))
                    });

                    codeType.Members.Add(new CodeMemberMethod()
                    {
                        Name = "RaisePropertyChanged",
                        Attributes = MemberAttributes.Family | MemberAttributes.Final,
                        Parameters =
                        {
                            new CodeParameterDeclarationExpression(typeof(string), "propertyName")
                        },
                        Statements =
                        {
                            new CodeVariableDeclarationStatement(typeof(PropertyChangedEventHandler), "propertyChanged",
                                new CodeEventReferenceExpression(new CodeThisReferenceExpression(), "PropertyChanged")),
                            new CodeConditionStatement(new CodeBinaryOperatorExpression(new CodeVariableReferenceExpression("propertyChanged"), CodeBinaryOperatorType.IdentityInequality, nullValue),
                                new CodeExpressionStatement(new CodeDelegateInvokeExpression(new CodeVariableReferenceExpression("propertyChanged"),
                                    new CodeThisReferenceExpression(),
                                    new CodeObjectCreateExpression(typeof(PropertyChangedEventArgs), new CodeArgumentReferenceExpression("propertyName")))))
                        }
                    });
                }

                if ((Options.AllTypesAreRoot || Options.AdditionalRootTypes.Contains(codeType.Name)) &&
                    !codeType.CustomAttributes.Cast<CodeAttributeDeclaration>().Any(x => x.Name == "System.Xml.Serialization.XmlRootAttribute"))
                {
                    var typeAttribute = codeType.CustomAttributes.Cast<CodeAttributeDeclaration>().FirstOrDefault(x => x.Name == "System.Xml.Serialization.XmlTypeAttribute");
                    var ns = typeAttribute?.Arguments?.Cast<CodeAttributeArgument>().FirstOrDefault(x => x.Name == "Namespace");
                    if (ns != null)
                    {
                        var rootAttribute = new CodeAttributeDeclaration("System.Xml.Serialization.XmlRootAttribute",
                            ns, new CodeAttributeArgument("IsNullable", new CodePrimitiveExpression(false)));

                        codeType.CustomAttributes.Add(rootAttribute);
                    }
                }

                bool mixedContentDetected = Options.MixedContent && members.ContainsKey("textField") && members.ContainsKey("itemsField");

                var binaryDataTypes = new[] { "hexBinary", "base64Binary" };

                bool IsItemsChoiceType(CodeTypeReference reference) => reference.BaseType.StartsWith("ItemsChoiceType");

                var fieldNameToPropertyMapping = members.Values.OfType<CodeMemberProperty>().Select(x => new
                {
                    Property = x,
                    FieldName = (x.GetStatements.OfType<CodeMethodReturnStatement>().SingleOrDefault()?.Expression as CodeFieldReferenceExpression)?.FieldName
                }).Where(x => x.FieldName != null).ToDictionary(x => x.FieldName, x => x.Property);

                var orderIndex = 0;
                foreach (CodeTypeMember member in members.Values)
                {
                    if (member is CodeMemberField)
                    {
                        CodeMemberField field = (CodeMemberField)member;

                        if (!fieldNameToPropertyMapping.TryGetValue(field.Name, out var backedProperty))
                            backedProperty = null;

                        bool isBinaryDataType = binaryDataTypes.Contains(backedProperty?.GetXmlDataType());
                        bool isItems = IsItemsChoiceType(field.Type) || backedProperty?.HasChoiceIdentifierAttribute() == true;

                        if (mixedContentDetected)
                        {
                            switch (field.Name)
                            {
                                case "textField":
                                    codeType.Members.Remove(member);
                                    continue;
                                case "itemsField":
                                    field.Type = new CodeTypeReference(typeof(object[]));
                                    break;
                            }
                        }

                        if (Options.UseLists && field.Type.ArrayRank > 0 && !isBinaryDataType && !isItems)
                        {
                            CodeTypeReference type = new CodeTypeReference(typeof(List<>))
                            {
                                TypeArguments =
                                {
                                    field.Type.ArrayElementType
                                }
                            };

                            field.Type = type;
                        }

                        if (codeType.IsEnum && Options.EnumValueCapitalizer != null)
                        {
                            var newName = Options.EnumValueCapitalizer.Capitalize(codeNamespace, member);
                            if (newName != member.Name)
                            {
                                SetAttributeOriginalName(member, member.GetOriginalName(), "System.Xml.Serialization.XmlEnumAttribute");
                                member.Name = newName;
                            }
                        }
                    }

                    if (member is CodeMemberProperty)
                    {
                        CodeMemberProperty property = (CodeMemberProperty)member;

                        // Is this "*Specified" property part of a "propertyName" and "propertyNameSpecified" combination?
                        var isSpecifiedProperty = property.Name.EndsWith("Specified") && members.ContainsKey(property.Name.Substring(0, property.Name.Length - 9));

                        bool isBinaryDataType = binaryDataTypes.Contains(property.GetXmlDataType());
                        bool isItems = IsItemsChoiceType(property.Type) || property.HasChoiceIdentifierAttribute();

                        if (mixedContentDetected)
                        {
                            switch (property.Name)
                            {
                                case "Text":
                                    codeType.Members.Remove(member);
                                    continue;
                                case "Items":
                                    property.Type = new CodeTypeReference(typeof(object[]));
                                    property.CustomAttributes.Add(new CodeAttributeDeclaration("System.Xml.Serialization.XmlTextAttribute", new CodeAttributeArgument { Name = "", Value = new CodeTypeOfExpression(new CodeTypeReference(typeof(string))) }));
                                    break;
                            }
                        }

                        string[] validXmlAttributeNames =
                        {
                            "System.Xml.Serialization.XmlEnumAttribute",
                            "System.Xml.Serialization.XmlTextAttribute",
                            "System.Xml.Serialization.XmlIgnoreAttribute",
                            "System.Xml.Serialization.XmlAttributeAttribute",
                            "System.Xml.Serialization.XmlElementAttribute",
                            "System.Xml.Serialization.XmlAnyAttributeAttribute",
                            "System.Xml.Serialization.XmlAnyElementAttribute",
                        };

                        var customAttributes = property.CustomAttributes.Cast<CodeAttributeDeclaration>();
                        var customAttributeNames = new HashSet<string>(customAttributes.Select(x => x.Name));
                        if (!customAttributeNames.Overlaps(validXmlAttributeNames))
                        {
                            // is this an array item?
                            bool arrayItem = property
                                .CustomAttributes.Cast<CodeAttributeDeclaration>()
                                .Any(x => x.Name == "System.Xml.Serialization.XmlArrayItemAttribute");
                            if (arrayItem)
                            {
                                property.CustomAttributes.Add(new CodeAttributeDeclaration
                                {
                                    Name = "System.Xml.Serialization.XmlArrayAttribute"
                                });
                            }
                            else
                            {
                                // It is implied that this is an xml element. Explicitly add the corresponding attribute.
                                property.CustomAttributes.Add(new CodeAttributeDeclaration
                                {
                                    Name = "System.Xml.Serialization.XmlElementAttribute"
                                });
                            }
                        }

                        if (Options.UseLists && property.Type.ArrayRank > 0 && !isBinaryDataType && !isItems)
                        {
                            CodeTypeReference type = new CodeTypeReference(typeof(List<>))
                            {
                                TypeArguments =
                                {
                                    property.Type.ArrayElementType
                                }
                            };

                            property.Type = type;
                        }

                        bool capitalizeProperty;
                        if (!isSpecifiedProperty)
                        {
                            if (Options.PreserveOrder)
                            {
                                if (!property.CustomAttributes.Cast<CodeAttributeDeclaration>().Any(x => nonElementAttributes.Contains(x.Name)))
                                {
                                    var elementAttributes = property
                                        .CustomAttributes.Cast<CodeAttributeDeclaration>()
                                        .Where(x => x.Name == "System.Xml.Serialization.XmlElementAttribute" || x.Name == "System.Xml.Serialization.XmlArrayAttribute")
                                        .ToList();
                                    if (elementAttributes.Count == 0)
                                    {
                                        // This should not happen (we implicitly add either XmlElementAttribute or XmlArrayAttribute above)
                                        throw new Exception("should not happen");
                                    }

                                    foreach (var elementAttribute in elementAttributes)
                                    {
                                        elementAttribute.Arguments.Add(new CodeAttributeArgument("Order", new CodePrimitiveExpression(orderIndex)));
                                    }

                                    orderIndex += 1;
                                }
                            }

                            if (Options.UseNullableTypes)
                            {
                                var fieldName = GetFieldName(property.Name, "Field");
                                CodeTypeMember specified;
                                if (members.TryGetValue(property.Name + "Specified", out specified))
                                {
                                    var nullableProperty = new CodeMemberProperty
                                    {
                                        Name = property.Name,
                                        Type = new CodeTypeReference(typeof(Nullable<>)) { TypeArguments = { property.Type.BaseType } },
                                        HasGet = true,
                                        HasSet = true,
                                        Attributes = MemberAttributes.Public | MemberAttributes.Final
                                    };

                                    nullableProperty.GetStatements.Add(
                                        new CodeConditionStatement(new CodeVariableReferenceExpression(fieldName + "Specified"),
                                            new CodeStatement[] { new CodeMethodReturnStatement(new CodeVariableReferenceExpression(fieldName)) },
                                            new CodeStatement[] { new CodeMethodReturnStatement(new CodePrimitiveExpression()) }
                                        ));

                                    nullableProperty.SetStatements.Add(
                                        new CodeConditionStatement(new CodeBinaryOperatorExpression(new CodePropertySetValueReferenceExpression(), CodeBinaryOperatorType.IdentityInequality, nullValue),
                                            new CodeStatement[]
                                            {
                                                new CodeAssignStatement(new CodeVariableReferenceExpression(fieldName + "Specified"),
                                                    new CodePrimitiveExpression(true)),
                                                new CodeAssignStatement(new CodeVariableReferenceExpression(fieldName),
                                                    new CodePropertyReferenceExpression(new CodePropertySetValueReferenceExpression(), "Value")),
                                            },
                                            new CodeStatement[]
                                            {
                                                new CodeAssignStatement(
                                                    new CodeVariableReferenceExpression(fieldName + "Specified"),
                                                    new CodePrimitiveExpression(false)),
                                            }
                                        ));

                                    nullableProperty.CustomAttributes.Add(new CodeAttributeDeclaration
                                    {
                                        Name = "System.Xml.Serialization.XmlIgnoreAttribute"
                                    });

                                    codeType.Members.Add(nullableProperty);

                                    foreach (CodeAttributeDeclaration attribute in property.CustomAttributes)
                                    {
                                        if (attribute.Name == "System.Xml.Serialization.XmlAttributeAttribute")
                                        {
                                            var firstArgument = attribute.Arguments.Cast<CodeAttributeArgument>().FirstOrDefault();
                                            if (firstArgument == null || !string.IsNullOrEmpty(firstArgument.Name))
                                            {
                                                attribute.Arguments.Add(new CodeAttributeArgument
                                                {
                                                    Name = "AttributeName",
                                                    Value = new CodePrimitiveExpression(property.Name)
                                                });
                                            }
                                        }
                                        else if (attribute.Name == "System.Xml.Serialization.XmlElementAttribute")
                                        {
                                            var firstArgument = attribute.Arguments.Cast<CodeAttributeArgument>().FirstOrDefault();
                                            if (firstArgument == null || !string.IsNullOrEmpty(firstArgument.Name))
                                            {
                                                attribute.Arguments.Add(new CodeAttributeArgument
                                                {
                                                    Name = "ElementName",
                                                    Value = new CodePrimitiveExpression(property.Name)
                                                });
                                            }
                                        }
                                    }

                                    property.Name = "_" + property.Name;
                                    specified.Name = "_" + specified.Name;

                                    if (Options.HideUnderlyingNullableProperties)
                                    {
                                        property.CustomAttributes.Add(neverBrowsableAttribute);
                                        specified.CustomAttributes.Add(neverBrowsableAttribute);
                                    }

                                    property = nullableProperty;
                                }
                            }

                            if (Options.EnableDataBinding)
                            {
                                property.SetStatements.Add(new CodeMethodInvokeExpression(new CodeThisReferenceExpression(), "RaisePropertyChanged", new CodePrimitiveExpression(property.Name)));
                            }

                            capitalizeProperty = Options.PropertyNameCapitalizer != null;
                        }
                        else if (!Options.UseNullableTypes)
                        {
                            if (Options.EnableDataBinding)
                            {
                                property.SetStatements.Add(new CodeMethodInvokeExpression(new CodeThisReferenceExpression(), "RaisePropertyChanged", new CodePrimitiveExpression(property.Name)));
                            }

                            capitalizeProperty = Options.PropertyNameCapitalizer != null;
                        }
                        else
                        {
                            capitalizeProperty = false;
                        }

                        if (capitalizeProperty)
                        {
                            var newName = Options.PropertyNameCapitalizer.Capitalize(codeNamespace, property);
                            if (newName != property.Name)
                            {
                                SetAttributeOriginalName(property, property.GetOriginalName(), "System.Xml.Serialization.XmlElementAttribute");
                                property.Name = newName;
                            }
                        }
                    }
                }
            }

            // Fixup changed type names
            if (changedTypeNames.Count != 0)
            {
                foreach (CodeTypeDeclaration codeType in codeNamespace.Types)
                {
                    if (codeType.IsEnum)
                        continue;

                    FixAttributeTypeReference(changedTypeNames, codeType);

                    foreach (CodeTypeMember member in codeType.Members)
                    {
                        var memberField = member as CodeMemberField;
                        if (memberField != null)
                        {
                            FixTypeReference(changedTypeNames, memberField.Type);
                            FixAttributeTypeReference(changedTypeNames, memberField);
                        }

                        var memberProperty = member as CodeMemberProperty;
                        if (memberProperty != null)
                        {
                            FixTypeReference(changedTypeNames, memberProperty.Type);
                            FixAttributeTypeReference(changedTypeNames, memberProperty);
                        }
                    }
                }
            }
        }

        private static void FixAttributeTypeReference(IReadOnlyDictionary<string, string> changedTypeNames, CodeTypeMember member)
        {
            foreach (CodeAttributeDeclaration attribute in member.CustomAttributes)
            {
                foreach (CodeAttributeArgument argument in attribute.Arguments)
                {
                    var typeOfExpr = argument.Value as CodeTypeOfExpression;
                    if (typeOfExpr != null)
                    {
                        FixTypeReference(changedTypeNames, typeOfExpr.Type);
                    }
                }
            }
        }

        private static void FixTypeReference(IReadOnlyDictionary<string, string> changedTypeNames, CodeTypeReference typeReference)
        {
            string newTypeName;
            if (!string.IsNullOrEmpty(typeReference.BaseType) && changedTypeNames.TryGetValue(typeReference.BaseType, out newTypeName))
            {
                typeReference.BaseType = newTypeName;
            }

            if (typeReference.ArrayElementType != null)
            {
                FixTypeReference(changedTypeNames, typeReference.ArrayElementType);
            }

            if (typeReference.TypeArguments != null && typeReference.TypeArguments.Count != 0)
            {
                foreach (CodeTypeReference typeArgument in typeReference.TypeArguments)
                {
                    FixTypeReference(changedTypeNames, typeArgument);
                }
            }
        }

        private static void SetAttributeOriginalName(CodeTypeMember member, string originalName, string newAttributeType)
        {
            var elementIgnored = false;
            var attributesThatNeedName = new List<CodeAttributeDeclaration>();
            foreach (CodeAttributeDeclaration attribute in member.CustomAttributes)
            {
                switch (attribute.Name)
                {
                    case "System.Xml.Serialization.XmlIgnoreAttribute":
                        elementIgnored = true;
                        break;
                    case "System.Xml.Serialization.XmlAttributeAttribute":
                    case "System.Xml.Serialization.XmlElementAttribute":
                    case "System.Xml.Serialization.XmlArrayItemAttribute":
                    case "System.Xml.Serialization.XmlEnumAttribute":
                    case "System.Xml.Serialization.XmlTypeAttribute":
                    case "System.Xml.Serialization.XmlRootAttribute":
                        attributesThatNeedName.Add(attribute);
                        break;
                }
            }

            if (elementIgnored)
                return;

            if (attributesThatNeedName.Count == 0)
            {
                var attribute = new CodeAttributeDeclaration(newAttributeType);
                attributesThatNeedName.Add(attribute);
                member.CustomAttributes.Add(attribute);
            }

            var nameArgument = new CodeAttributeArgument { Name = "", Value = new CodePrimitiveExpression(originalName) };

            foreach (var attribute in attributesThatNeedName)
            {
                switch (attribute.Name)
                {
                    case "System.Xml.Serialization.XmlTypeAttribute":
                        if (attribute.IsAnonymousTypeArgument())
                            continue;
                        break;
                }

                var hasNameAttribute = attribute.Arguments.Cast<CodeAttributeArgument>().Any(x => x.IsNameArgument());
                if (!hasNameAttribute)
                    attribute.Arguments.Insert(0, nameArgument);
            }
        }

        private static string GetFieldName(string p, string suffix = null)
        {
            return p.Substring(0, 1).ToLower() + p.Substring(1) + suffix;
        }

        private class UsageTree
        {
            private readonly ILookup<string, Reference> _tree;

            public UsageTree(CodeNamespace codeNamespace)
            {
                _tree = BuildReferences(codeNamespace).ToLookup(x => GetElementType(x.Item1), x => x.Item2);
            }

            public IEnumerable<Reference> LookupUsages(CodeTypeDeclaration typeDeclaration)
            {
                return _tree[typeDeclaration.Name];
            }

            private static IEnumerable<Tuple<CodeTypeReference, Reference>> BuildReferences(CodeNamespace codeNamespace)
            {
                foreach (CodeTypeDeclaration codeType in codeNamespace.Types)
                {
                    foreach (CodeTypeMember member in codeType.Members)
                    {
                        if (member is CodeMemberProperty property)
                        {
                            var reference = new Reference(codeType, property);

                            yield return Tuple.Create(property.Type, reference);

                            foreach (var xmlType in property.GetXmlTypes())
                            {
                                yield return Tuple.Create(xmlType, reference);
                            }
                        }
                    }
                }
            }

            private static string GetElementType(CodeTypeReference typeReference)
            {
                if (typeReference.ArrayRank != 0)
                    return typeReference.ArrayElementType.BaseType;
                if (typeReference.BaseType.EndsWith(".List`1", StringComparison.Ordinal))
                    return typeReference.TypeArguments[0].BaseType;
                return typeReference.BaseType;
            }

            public struct Reference
            {
                public Reference(CodeTypeDeclaration type, CodeMemberProperty property)
                {
                    Type = type;
                    Property = property;
                }

                public CodeTypeDeclaration Type { get; }

                public CodeMemberProperty Property { get; }
            }
        }
    }
}
