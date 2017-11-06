namespace Microsoft.OpenPublishing.Build.Applications.ConfigurationSchemaGenerator
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;

    using CommandLine;
    using Microsoft.OpenPublishing.Build.DataContracts;
    using Microsoft.OpenPublishing.Build.DataContracts.GitModel;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Serialization;

    public class Program
    {
        private static readonly Parser Parser = new Parser(config => config.HelpWriter = Console.Out);

        // serialize the schema
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new CustomResolver(),
            PreserveReferencesHandling = PreserveReferencesHandling.None,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
        };

        public static void Main(string[] args)
        {
            // parse the input args
            var options = Parser.ParseArguments<Options>(args).MapResult(
                option =>
                {
                    // prepare options
                    if (option.InputSchemaPath == null)
                    {
                        var defaultSchemaPath = Path.Combine(Directory.GetCurrentDirectory(), option.StandardSchema ? "stand_schema.json" : "schema.json");
                        Console.WriteLine($"Using default input schema path: {defaultSchemaPath}");
                        option.InputSchemaPath = defaultSchemaPath;
                    }

                    if (option.OutputSchemaPath == null)
                    {
                        Console.WriteLine($"Output schema path would be the same with input schema path: {option.InputSchemaPath}");
                        option.OutputSchemaPath = option.InputSchemaPath;
                    }

                    if (option.StandardSchema)
                    {
                        // get standard schema from ops configuration
                        var opsStandardSchema = GetStandardSchema(null, typeof(PublishConfig), "ops_configuration", null, "OPS");

                        // get standard shcema from docfx configuration
                        var docfxStandardSchema = GetStandardSchema(null, typeof(DocfxBuild), "docfx_configuration", null, "Docfx");

                        foreach (var docfxProperty in docfxStandardSchema.Properties)
                        {
                            opsStandardSchema.Properties["docsets_to_publish"].Items.Properties.Add(docfxProperty);
                        }

                        Settings.DefaultValueHandling = DefaultValueHandling.Ignore;
                        File.WriteAllText(option.OutputSchemaPath, JsonConvert.SerializeObject(opsStandardSchema, Settings));
                        Console.WriteLine($"Done! Standard schema file was generated at {option.OutputSchemaPath}");
                        return 0;
                    }

                    // get schema from ops config
                    var schemas = GetSchemas(null, typeof(PublishConfig), "OPS");

                    // get schema from docfx config
                    schemas.AddRange(GetSchemas("docsets_to_publish", typeof(DocfxBuild), "DocFX"));
                    var schemasGroubByName = schemas.ToDictionary(k => k.Name, v => v);

                    // merge existing schema if it exists
                    var existingSchemasGroupByName = new Dictionary<string, SchemaExtension>();
                    if (File.Exists(option.InputSchemaPath))
                    {
                        existingSchemasGroupByName = JsonConvert.DeserializeObject<Dictionary<string, SchemaExtension>>(File.ReadAllText(option.InputSchemaPath));
                    }

                    // replace new schemas
                    foreach (var schema in schemasGroubByName)
                    {
                        existingSchemasGroupByName[schema.Key] = schema.Value;
                    }

                    // remove deleted ones
                    existingSchemasGroupByName = existingSchemasGroupByName.Where(kvp => (kvp.Value.From != "OPS" && kvp.Value.From != "DocFX") || schemasGroubByName.ContainsKey(kvp.Key)).ToDictionary(k => k.Key, v => v.Value);

                    File.WriteAllText(option.OutputSchemaPath, JsonConvert.SerializeObject(existingSchemasGroupByName.OrderBy(_ => _.Key).ToDictionary(k => k.Key, v => v.Value), Settings));
                    Console.WriteLine($"Done! Schema file was generated at {option.OutputSchemaPath}");

                    return 0;
                },
                _ => 1);
        }

        private static List<SchemaExtension> GetSchemas(string parent, Type type, string from)
        {
            var schemas = new List<SchemaExtension>();

            foreach (var member in type.GetProperties())
            {
                var name = GetName(member);
                if (name == null)
                {
                    continue;
                }

                var schemaAttribute = GetSchemaAttribute(member);
                if (schemaAttribute == null)
                {
                    continue;
                }

                var nameWithParents = string.IsNullOrEmpty(parent) ? name : $"{parent}.{name}";
                if (member.PropertyType.IsGenericType && member.PropertyType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    var innerType = member.PropertyType.GetGenericArguments()[0];
                    schemas.AddRange(GetSchemas(nameWithParents, innerType, from));
                }

                if (member.PropertyType.IsArray && member.PropertyType.GetElementType() == typeof(Build.DataContracts.GitModel.FileItem))
                {
                    var innerType = member.PropertyType.GetElementType();
                    schemas.AddRange(GetSchemas(nameWithParents, innerType, from));
                }

                schemas.Add(new SchemaExtension(nameWithParents, GetTypeName(member), IsNullable(member.PropertyType), from, schemaAttribute));
            }

            return schemas;
        }

        private static StandardSchema GetStandardSchema(StandardSchema root, Type type, string name, SchemaAttribute schemaAttribute, string from)
        {
            var standardSchema = (name == null || schemaAttribute == null) ? new StandardSchema(GetTypeName(type), name) : new StandardSchema(name, GetTypeName(type), IsNullable(type), from, schemaAttribute);

            // todo: add re-used definition
            root = root ?? standardSchema;
            root.Schema = "http://json-schema.org/draft-04/schema#";

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var innerType = type.GetGenericArguments()[0];
                standardSchema.Items = GetStandardSchema(root, innerType, null, null, from);
            }

            if (type.IsArray)
            {
                var innerType = type.GetElementType();
                standardSchema.Items = GetStandardSchema(root, innerType, null, null, from);
            }

            var properties = type.GetProperties().Where(p => GetName(p) != null && GetSchemaAttribute(p) != null);

            if (properties.Any())
            {
                standardSchema.Properties = properties.ToDictionary(k => GetName(k), v => GetStandardSchema(root, v.PropertyType, GetName(v), GetSchemaAttribute(v), from));
            }

            return standardSchema;
        }

        private static SchemaAttribute GetSchemaAttribute(MemberInfo info)
        {
            return info.GetCustomAttributes(typeof(SchemaAttribute), false).FirstOrDefault() as SchemaAttribute;
        }

        private static bool IsNullable(Type type)
        {
            return Nullable.GetUnderlyingType(type) != null || type.IsClass;
        }

        private static string GetName(MemberInfo info)
        {
            var jsonPropertyAttribute = info.GetCustomAttributes(typeof(JsonPropertyAttribute), false).FirstOrDefault() as JsonPropertyAttribute;
            var name = jsonPropertyAttribute?.PropertyName;

            return name;
        }

        private static string GetTypeName(PropertyInfo propertyInfo)
        {
            return GetTypeName(propertyInfo.PropertyType);
        }

        private static string[] _reservedTypeNames = new[] { "string", "array", "object", "boolean", "number", "null" };

        private static string GetTypeName(Type type)
        {
            var typeStr = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>) ? type.GetGenericArguments()[0].Name : type.Name;
            typeStr = typeStr.Split('`')[0];

            if (typeStr.EndsWith("[]") || typeStr == "List")
            {
                typeStr = "array";
            }

            if (!_reservedTypeNames.Contains(typeStr.ToLowerInvariant()))
            {
                typeStr = "object";
            }

            return typeStr.ToLowerInvariant();
        }
    }

    internal class CustomResolver : DefaultContractResolver
    {
        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            JsonProperty prop = base.CreateProperty(member, memberSerialization);
            var propInfo = member as PropertyInfo;
            if (propInfo != null)
            {
                if (propInfo.GetMethod.IsVirtual && !propInfo.GetMethod.IsFinal)
                {
                    prop.ShouldSerialize = obj => false;
                }
            }
            return prop;
        }
    }
}
