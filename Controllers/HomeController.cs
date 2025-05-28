using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Azure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Amqp.Framing;
using Microsoft.Data.SqlClient;
using Microsoft.DotNet.Scaffolding.Shared.CodeModifier.CodeChange;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MyModelly.Models;
using NuGet.Packaging;
using NuGet.Packaging.Signing;

namespace MyModelly.Controllers
{
    public class HomeController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<HomeController> _logger;

        public HomeController(ILogger<HomeController> logger, IConfiguration configuration)
        {
            _configuration = configuration;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> ShowDatabases()
        {
            var connectionString = _configuration.GetConnectionString("DefaultConnection");

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                var databases = new List<string>();

                using (var command = new SqlCommand("SELECT name FROM sys.databases", connection))
                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var databaseName = reader.GetString(0);
                        databases.Add(databaseName);
                    }
                }

                return Ok(databases);
            }
        }

        [HttpPost]
        public async Task<IActionResult> ShowTables()
        {

            var selectedDatabase = Request.Query["selectedDatabase"];
            if (string.IsNullOrEmpty(selectedDatabase))
            {
                // Handle the case where no database is selected
                return RedirectToAction("ShowDatabases");
            }

            var connectionString = _configuration.GetConnectionString("DefaultConnection");

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                var tables = new List<string>();

                // Use the selected database in the query
                using (var command = new SqlCommand($"SELECT table_name FROM {selectedDatabase}.information_schema.tables", connection))
                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var tableName = reader.GetString(0);


                        tables.Add(tableName);
                    }
                }
                return Ok(tables);
            }
        }


        [HttpPost]
        public async Task<IActionResult> GenerateCode([FromForm] string selectedDatabase, List<string> selectedTables)
        {
            try
            {
                if (string.IsNullOrEmpty(selectedDatabase) || selectedTables == null || selectedTables.Count == 0)
                {
                    // Handle the case where no database or table is selected
                    return RedirectToAction("ShowDatabases");
                }

                var connectionString = _configuration.GetConnectionString("DefaultConnection");
                Assembly assembly = Assembly.GetExecutingAssembly();

                // Get the namespace by extracting the assembly's full name
                string assemblyFullName = assembly.FullName;
                string[] parts = assemblyFullName.Split(',');

                // The namespace is the first part of the assembly's full name
                string currentNamespace = parts[0].Trim();

                using (var connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync();

                    var successList = new List<bool>();

                    // Create a temporary directory for storing the generated files
                    string tempFolderPath = Path.Combine(Path.GetTempPath(), "GeneratedFiles", Guid.NewGuid().ToString());
                    Directory.CreateDirectory(tempFolderPath);

                    foreach (var selectedTableName in selectedTables)
                    {
                        var columns = new List<ColumnModel>();

                        using (var command = new SqlCommand($"SELECT COLUMN_NAME, DATA_TYPE, COLUMNPROPERTY(object_id(TABLE_NAME), COLUMN_NAME, 'IsIdentity') as IS_IDENTITY, " +
                                         $"CASE WHEN COLUMN_NAME IN (SELECT COLUMN_NAME FROM {selectedDatabase}.information_schema.key_column_usage WHERE TABLE_NAME = @tableName) THEN 1 ELSE 0 END AS IS_PRIMARY_KEY " +
                                         $"FROM {selectedDatabase}.information_schema.columns WHERE TABLE_NAME = @tableName", connection))
                        {
                            command.Parameters.AddWithValue("@tableName", selectedTableName);

                            using (var reader = await command.ExecuteReaderAsync())
                            {
                                while (await reader.ReadAsync())
                                {
                                    var columnName = reader.GetString(0);
                                    var dataType = reader.GetString(1);
                                    var isIdentity = reader.GetInt32(2) == 1;
                                    var isPrimaryKey = reader.GetInt32(3) == 1;

                                    columns.Add(new ColumnModel { Name = columnName, DataType = dataType, IsAutoIncrement = isIdentity, IsPrimaryKey = isPrimaryKey });
                                }
                            }
                        }

                        var table = new Table
                        {
                            Name = selectedTableName,
                            Namespace = $"{currentNamespace}",
                            EntityClass = selectedTableName,
                            Columns = columns
                        };

                        var tableFolder = Path.Combine(tempFolderPath, selectedTableName);
                        Directory.CreateDirectory(tableFolder);

                        // Create .NET folder for C# files
                        var dotNetFolderPath = Path.Combine(tableFolder, "Net");
                        Directory.CreateDirectory(dotNetFolderPath);

                        // Create Angular folder for typescript files
                        var angularFolderPath = Path.Combine(tableFolder, "Angular");
                        Directory.CreateDirectory(angularFolderPath);

                        // Generate and save config file
                        var configCode = GenerateConfigFile(table);
                        var configFilePath = Path.Combine(dotNetFolderPath, "Config.cs");
                        System.IO.File.WriteAllText(configFilePath, configCode);

                        // Generate and save files for the current table
                        GenerateFilesForTable(table, selectedDatabase, dotNetFolderPath, angularFolderPath);
                    }

                    // Generate a unique filename for the ZIP file
                    string zipFileName = $"GeneratedFiles_{DateTime.Now:yyyyMMddHHmmss}.zip";

                    // Create a ZIP archive containing the contents of the temporary folder
                    string tempZipFilePath = Path.Combine(Path.GetTempPath(), zipFileName);
                    ZipFile.CreateFromDirectory(tempFolderPath, tempZipFilePath);

                    // Return the ZIP file to the client
                    return File(System.IO.File.ReadAllBytes(tempZipFilePath), "application/zip", zipFileName);
                }
            }
            catch (Exception ex)
            {
                // Handle exceptions
                return Json(new { success = false, errorMessage = ex.Message, stackTrace = ex.StackTrace });
            }
        }


        private void GenerateFilesForTable(Table table, string selectedDatabase, string dotNetFolderPath, string angularFolderPath)
        {
            //C# files
            // Generate C# class code and save to file
            var classCode = GenerateCSharpClass(table, selectedDatabase);
            var entityFilePath = Path.Combine(dotNetFolderPath, $"{table.Name}Entity.cs");
            System.IO.File.WriteAllText(entityFilePath, classCode);

            // Generate and save access file
            var accessCode = GenerateAccessCode(table, selectedDatabase);
            var accessFilePath = Path.Combine(dotNetFolderPath, $"{table.Name}Access.cs");
            System.IO.File.WriteAllText(accessFilePath, accessCode);

            // Generate and save service files
            var serviceInterfaceCode = GenerateServiceInterfaceCode(table, selectedDatabase);
            var serviceClassCode = GenerateServiceClassCode(table, selectedDatabase);
            var serviceFolderPath = Path.Combine(dotNetFolderPath, "Services");
            Directory.CreateDirectory(serviceFolderPath);
            var serviceInterfaceFilePath = Path.Combine(serviceFolderPath, $"I{table.Name}Service.cs");
            var serviceClassFilePath = Path.Combine(serviceFolderPath, $"{table.Name}Service.cs");
            System.IO.File.WriteAllText(serviceInterfaceFilePath, serviceInterfaceCode);
            System.IO.File.WriteAllText(serviceClassFilePath, serviceClassCode);

            // Generate and save controller file
            var controllerCode = GenerateControllerCode(table, selectedDatabase);
            var controllerFilePath = Path.Combine(dotNetFolderPath, $"{table.Name}Controller.cs");
            System.IO.File.WriteAllText(controllerFilePath, controllerCode);



            //Typescript files
            // Generate and save TypeScript interface file
            var interfaceCode = GenerateInterfaceClass(table);
            var interfaceFilePath = Path.Combine(angularFolderPath, $"{table.Name.ToLower()}.ts");
            System.IO.File.WriteAllText(interfaceFilePath, interfaceCode);

            // Generate and save service test file
            var serviceAngularFolderPath = Path.Combine(angularFolderPath, $"{table.Name.ToLower()}-service");
            Directory.CreateDirectory(serviceAngularFolderPath);
            var serviceTestCode = GenerateAngularTestServiceCode(table);
            var serviceTestFilePath = Path.Combine(serviceAngularFolderPath, $"{table.Name.ToLower()}.service.spec.ts");
            System.IO.File.WriteAllText(serviceTestFilePath, serviceTestCode);
            // Generate and save service file
            var serviceCode = GenerateAngularServiceCode(table);
            var serviceFilePath = Path.Combine(serviceAngularFolderPath, $"{table.Name.ToLower()}.service.ts");
            System.IO.File.WriteAllText(serviceFilePath, serviceCode);

            //Generate html snippet for Angular material table
            var matTableFolderPath = Path.Combine(angularFolderPath, $"{table.Name.ToLower()}-mat-table");
            Directory.CreateDirectory(matTableFolderPath);
            var angularMaterialTableCode = GenerateAngularMaterialTable(table);
            var angularMaterialTablePath = Path.Combine(matTableFolderPath, $"{table.Name.ToLower()}MatTable.html");
            System.IO.File.WriteAllText(angularMaterialTablePath, angularMaterialTableCode);

            //Generate Angular material table component
            var angularMaterialTableComponentCode = GenerateAngularMaterialTableComponent(table);
            var angularMaterialTableComponentPath = Path.Combine(matTableFolderPath, $"{table.Name.ToLower()}MatTable.ts");
            System.IO.File.WriteAllText(angularMaterialTableComponentPath, angularMaterialTableComponentCode);

            //Generate CRUD components for table
            //Generate component for insert method
            var insertComponentFolderPath = Path.Combine(angularFolderPath, $"{table.Name.ToLower()}-insert");
            Directory.CreateDirectory(insertComponentFolderPath);
            //Typescript Spec file
            var insertTypescriptSpecCode = GenerateTypeScriptSpecForComponent(table, "Insert");
            var insertTypescriptSpecPath = Path.Combine(insertComponentFolderPath, $"{table.Name.ToLower()}-insert.component.spec.ts");
            System.IO.File.WriteAllText(insertTypescriptSpecPath, insertTypescriptSpecCode);
            //Typescript file
            var insertTypescriptCode = GenerateTypescriptForComponent(table, "Insert");
            var insertTypescriptPath = Path.Combine(insertComponentFolderPath, $"{table.Name.ToLower()}-insert.component.ts");
            System.IO.File.WriteAllText(insertTypescriptPath, insertTypescriptCode);
            //CSS file
            var insertCSSCode = GenerateCSSFileForComponent(table, "Insert");
            var insertCSSPath = Path.Combine(insertComponentFolderPath, $"{table.Name.ToLower()}-insert.component.css");
            System.IO.File.WriteAllText(insertCSSPath, insertCSSCode);
            //HTML file
            var insertHTMLCode = GenerateInsertHtml(table);
            var insertHTMLPath = Path.Combine(insertComponentFolderPath, $"{table.Name.ToLower()}-insert.component.html");
            System.IO.File.WriteAllText(insertHTMLPath, insertHTMLCode);

            //Generate component for update method
            var updateComponentFolderPath = Path.Combine(angularFolderPath, $"{table.Name.ToLower()}-update");
            Directory.CreateDirectory(updateComponentFolderPath);
            //Typescript Spec file
            var updateTypescriptSpecCode = GenerateTypeScriptSpecForComponent(table, "Update");
            var updateTypescriptSpecPath = Path.Combine(updateComponentFolderPath, $"{table.Name.ToLower()}-update.component.spec.ts");
            System.IO.File.WriteAllText(updateTypescriptSpecPath, updateTypescriptSpecCode);
            //Typescript file
            var updateTypescriptCode = GenerateTypescriptForComponent(table, "Update");
            var updateTypescriptPath = Path.Combine(updateComponentFolderPath, $"{table.Name.ToLower()}-update.component.ts");
            System.IO.File.WriteAllText(updateTypescriptPath, updateTypescriptCode);
            //CSS file
            var updateCSSCode = GenerateCSSFileForComponent(table, "Update");
            var updateCSSPath = Path.Combine(updateComponentFolderPath, $"{table.Name.ToLower()}-update.component.css");
            System.IO.File.WriteAllText(updateCSSPath, updateCSSCode);
            //HTML file
            var updateHTMLCode = GenerateUpdateHtml(table);
            var updateHTMLPath = Path.Combine(updateComponentFolderPath, $"{table.Name.ToLower()}-update.component.html");
            System.IO.File.WriteAllText(updateHTMLPath, updateHTMLCode);

            //Generate component for delete method
            var deleteComponentFolderPath = Path.Combine(angularFolderPath, $"{table.Name.ToLower()}-delete");
            Directory.CreateDirectory(deleteComponentFolderPath);
            //Typescript Spec file
            var deleteTypescriptSpecCode = GenerateTypeScriptSpecForComponent(table, "Delete");
            var deleteTypescriptSpecPath = Path.Combine(deleteComponentFolderPath, $"{table.Name.ToLower()}-delete.component.spec.ts");
            System.IO.File.WriteAllText(deleteTypescriptSpecPath, deleteTypescriptSpecCode);
            //Typescript file
            var deleteTypescriptCode = GenerateTypescriptForComponent(table, "Delete");
            var deleteTypescriptPath = Path.Combine(deleteComponentFolderPath, $"{table.Name.ToLower()}-delete.component.ts");
            System.IO.File.WriteAllText(deleteTypescriptPath, deleteTypescriptCode);
            //CSS file
            var deleteCSSCode = GenerateCSSFileForComponent(table, "Delete");
            var deleteCSSPath = Path.Combine(deleteComponentFolderPath, $"{table.Name.ToLower()}-delete.component.css");
            System.IO.File.WriteAllText(deleteCSSPath, deleteCSSCode);
            //HTML file
            var deleteHTMLCode = GenerateDeleteHtml(table);
            var deleteHTMLPath = Path.Combine(deleteComponentFolderPath, $"{table.Name.ToLower()}-delete.component.html");
            System.IO.File.WriteAllText(deleteHTMLPath, deleteHTMLCode);

            //Generate component for get method
            var getComponentFolderPath = Path.Combine(angularFolderPath, $"{table.Name.ToLower()}-get");
            Directory.CreateDirectory(getComponentFolderPath);
            //Typescript Spec file
            var getTypescriptSpecCode = GenerateTypeScriptSpecForComponent(table, "GetById");
            var getTypescriptSpecPath = Path.Combine(getComponentFolderPath, $"{table.Name.ToLower()}-get.component.spec.ts");
            System.IO.File.WriteAllText(getTypescriptSpecPath, getTypescriptSpecCode);
            //Typescript file
            var getTypescriptCode = GenerateTypescriptForComponent(table, "GetById");
            var getTypescriptPath = Path.Combine(getComponentFolderPath, $"{table.Name.ToLower()}-get.component.ts");
            System.IO.File.WriteAllText(getTypescriptPath, getTypescriptCode);
            //CSS file
            var getCSSCode = GenerateCSSFileForComponent(table, "GetById");
            var getCSSPath = Path.Combine(getComponentFolderPath, $"{table.Name.ToLower()}-get.component.css");
            System.IO.File.WriteAllText(getCSSPath, getCSSCode);
            //HTML file
            var getHTMLCode = GenerateGetByIdHtml(table);
            var getHTMLPath = Path.Combine(getComponentFolderPath, $"{table.Name.ToLower()}-get.component.html");
            System.IO.File.WriteAllText(getHTMLPath, getHTMLCode);

        }



        private string GenerateCSharpClass(Table table, string database)
        {
            var classCode = "using System;\n";
            classCode += "using System.ComponentModel;\n";
            classCode += "using System.Data;\n";
            classCode += "using System.ComponentModel.DataAnnotations;\n";

            // Check if Microsoft.SqlServer.Types are needed
            if (UsesSqlServerTypes(table))
            {
                classCode += "using Microsoft.SqlServer.Types;\n";
            }

            classCode += $"namespace {table.Namespace}.Database.Entities.{database}\n";
            classCode += $"{{\n";
            classCode += $"    public class {table.Name}\n";
            classCode += $"    {{\n";

            // Properties
            foreach (var column in table.Columns)
            {
                classCode += $"            {GetCSharpAnnotations(column)}\n";
                classCode += $"        public {GetCSharpType(column.DataType)} {column.Name} {{ get; set; }}\n";
            }

            classCode += $"        public {table.Name}(){{}}\n";

            // Constructor with parameters for all properties
            classCode += $"        public {table.Name}(";

            for (int i = 0; i < table.Columns.Count; i++)
            {
                var column = table.Columns[i];
                classCode += $"{GetCSharpType(column.DataType)} {column.Name}";

                if (i < table.Columns.Count - 1)
                {
                    classCode += ", ";
                }
            }

            classCode += ")\n";
            classCode += $"        {{\n";

            // Set the properties in the constructor
            foreach (var column in table.Columns)
            {
                classCode += $"            this.{column.Name} = {column.Name};\n";
            }

            classCode += $"        }}\n";

            classCode += GenerateConstructor(table);

            // ToString Method
            classCode += $"        public override string ToString()\n";
            classCode += $"        {{\n";
            classCode += $"            return $\"{table.Name} - {string.Join(", ", table.Columns.Select(c => $"{c.Name}: {{{c.Name}}}"))} \";\n";
            classCode += $"        }}\n";

            classCode += $"    }}\n";
            classCode += $"}}\n";

            return classCode;
        }

        private bool UsesSqlServerTypes(Table table)
        {
            foreach (var column in table.Columns)
            {
                switch (column.DataType.ToLower())
                {
                    case "geography":
                    case "geometry":
                    case "hierarchyid":
                        return true;
                }
            }

            return false;
        }



        private string GetCSharpType(string dataType)
        {
            switch (dataType.ToLower())
            {
                case "int":
                case "int identity":
                    return "int";
                case "bigint":
                    return "long";
                case "smallint":
                    return "short";
                case "tinyint":
                    return "byte";
                case "decimal":
                case "numeric":
                case "money":
                case "smallmoney":
                    return "decimal";
                case "float":
                    return "double";
                case "real":
                    return "Single";
                case "nvarchar":
                case "varchar":
                case "char":
                case "nchar":
                case "text":
                case "ntext":
                case "xml":
                case "json":
                case "varchar(max)":
                case "nvarchar(max)":
                    return "string";
                case "date":
                case "datetime":
                case "datetime2":
                case "smalldatetime":
                    return "DateTime";
                case "uniqueidentifier":
                    return "Guid";
                case "bit":
                    return "bool";
                case "binary":
                case "varbinary":
                case "rowversion":
                case "timestamp":
                case "image":
                case "varbinary(max)":
                case "filestream":
                    return "byte[]";
                case "time":
                    return "TimeSpan";
                case "datetimeoffset":
                    return "DateTimeOffset";
                case "geography":
                    return "Microsoft.SqlServer.Types.SqlGeography";
                case "geometry":
                    return "Microsoft.SqlServer.Types.SqlGeometry";
                case "hierarchyid":
                    return "Microsoft.SqlServer.Types.SqlHierarchyId";
                case "sql_variant":
                    return "object";
                case "table":
                    return "DataTable";
                case "sql_variant_array":
                    return "object[]";
                default:
                    return "object";
            }
        }



        private string GenerateConstructor(Table table)
        {
            var constructorCode = $"        public {table.Name}(DataRow row)\n";
            constructorCode += $"        {{\n";

            foreach (var column in table.Columns)
            {
                constructorCode += $"            this.{column.Name} = row.Field<{GetCSharpType(column.DataType)}>(\"{column.Name}\");\n";

            }

            constructorCode += $"        }}\n";

            return constructorCode;
        }



        private string GetCSharpAnnotations(ColumnModel column)
        {
            // Use a dictionary to map SQL data types to C# attributes
            var annotations = new Dictionary<string, string>
    {
        {"bit", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"tinyint", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"smallint", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"int", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"bigint", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"decimal", "[DataType(DataType.Currency)]\n            [Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"numeric", "[DataType(DataType.Currency)]\n            [Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"float", "[DataType(DataType.Currency)]\n            [Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"real", "[DataType(DataType.Currency)]\n            [Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"nvarchar", "[MaxLength(255)]\n            [Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"varchar", "[MaxLength(255)]\n            [Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"char", "[MaxLength(1)]\n            [Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"nchar", "[MaxLength(1)]\n            [Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"datetime", "[DataType(DataType.DateTime)]\n            [Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"datetime2", "[DataType(DataType.DateTime)]\n            [Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"smalldatetime", "[DataType(DataType.DateTime)]\n            [Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"uniqueidentifier", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"binary", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"text", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"ntext", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"xml", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"money", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"smallmoney", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"datetimeoffset", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"object", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"geography", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"geometry", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"hierarchyid", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"sql_variant", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"table", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"sql_variant_array", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"image", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"rowversion", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"timestamp", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"varbinary(max)", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"filestream", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"time", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        {"byte", "[Required(ErrorMessage = \"The " + column.Name + " field is required.\")]"},
        // Add annotations for other missing data types here
    };

            return annotations.ContainsKey(column.DataType.ToLower()) ? annotations[column.DataType.ToLower()] : string.Empty;
        }



        private string GenerateConfigFile(Table table)
        {
            var configCode = "using System;\n";
            configCode += "using System.Collections.Generic;\n";
            configCode += "using System.Data;\n";
            configCode += "using System.Text;\n\n";

            configCode += $"namespace {table.Namespace}.Database.Access\n";
            configCode += "{\n";
            configCode += "    public interface IConfig\n";
            configCode += "    {\n";
            configCode += "    }\n";
            configCode += "    public class Config : IConfig\n";
            configCode += "    {\n";
            configCode += "        public const int MAX_BATCH_SIZE = 1000;\n";
            configCode += "        public static string _connectionString { get; set; } = \"\";\n";
            configCode += $"        public Config(string connectionString)\n";
            configCode += "        {\n";
            configCode += "            _connectionString = connectionString;\n";
            configCode += "        }\n\n";
            configCode += "        public static string GetConnectionString()\n";
            configCode += "        {\n";
            configCode += "            return _connectionString;\n";
            configCode += "        }\n";
            configCode += "    }\n";
            configCode += "}\n";

            return configCode;
        }



        public string GenerateAccessCode(Table table, string database)
        {
            StringBuilder code = new StringBuilder();

            // Namespace and class declaration
            code.AppendLine("using System;");
            code.AppendLine("using System.Collections.Generic;");
            code.AppendLine("using System.Data;");
            code.AppendLine("using System.Linq;");
            code.AppendLine("using Microsoft.Data.SqlClient;");
            code.AppendLine($"using {table.Namespace}.Database.Entities.{database};");
            code.AppendLine();
            code.AppendLine($"namespace {table.Namespace}.Database.Access.{database}");
            code.AppendLine("{");
            code.AppendLine($"    public class {table.Name}Access");
            code.AppendLine("    {");

            // Default Methods region
            code.AppendLine("        #region Default Methods");

            // Get method
            code.AppendLine($"      public {table.EntityClass} Get(int id)");
            code.AppendLine("       {");
            code.AppendLine("           var dataTable = new DataTable();");
            code.AppendLine($"          using (var sqlConnection = new SqlConnection({table.Namespace}.Database.Access.Config.GetConnectionString()))");
            code.AppendLine("           {");
            code.AppendLine("               sqlConnection.Open();");

            // Assuming the primary key column is stored in the 'table' object
            string primaryKeyColumnName = table.Columns.FirstOrDefault(c => c.IsPrimaryKey)?.Name ?? "Id";

            code.AppendLine($"              string query = \"SELECT * FROM [{table.Name}] WHERE [{primaryKeyColumnName}]=@{primaryKeyColumnName}\";");
            code.AppendLine("               var sqlCommand = new SqlCommand(query, sqlConnection);");
            code.AppendLine($"              sqlCommand.Parameters.AddWithValue(\"@{primaryKeyColumnName}\", id);");
            code.AppendLine();
            code.AppendLine("               new SqlDataAdapter(sqlCommand).Fill(dataTable);");
            code.AppendLine("           }");
            code.AppendLine();
            code.AppendLine("           if (dataTable.Rows.Count > 0)");
            code.AppendLine("           {");
            code.AppendLine($"              return new {table.EntityClass}(dataTable.Rows[0]);");
            code.AppendLine("           }");
            code.AppendLine("           else");
            code.AppendLine("           {");
            code.AppendLine("               return null;");
            code.AppendLine("           }");
            code.AppendLine("       }");


            // Get all method
            code.AppendLine($"        public List<{table.EntityClass}> GetAll()");
            code.AppendLine("        {");
            code.AppendLine("            var dataTable = new DataTable();");
            code.AppendLine($"            using (var sqlConnection = new SqlConnection({table.Namespace}.Database.Access.Config.GetConnectionString()))");
            code.AppendLine("            {");
            code.AppendLine("                sqlConnection.Open();");
            code.AppendLine($"                string query = \"SELECT * FROM [{table.Name}]\";");
            code.AppendLine("                var sqlCommand = new SqlCommand(query, sqlConnection);");
            code.AppendLine();
            code.AppendLine("                new SqlDataAdapter(sqlCommand).Fill(dataTable);");
            code.AppendLine("            }");
            code.AppendLine();
            code.AppendLine("            if (dataTable.Rows.Count > 0)");
            code.AppendLine("            {");
            code.AppendLine($"                return dataTable.Rows.Cast<DataRow>().Select(x => new {table.EntityClass}(x)).ToList();");
            code.AppendLine("            }");
            code.AppendLine("            else");
            code.AppendLine("            {");
            code.AppendLine($"                return new List<{table.EntityClass}>();");
            code.AppendLine("            }");
            code.AppendLine("        }");

            // Get multiple method
            code.AppendLine($"        private List<{table.EntityClass}> getMultiple(List<int> ids)");
            code.AppendLine("        {");
            code.AppendLine("            if (ids != null && ids.Count > 0)");
            code.AppendLine("            {");
            code.AppendLine($"                var dataTable = new DataTable();");
            code.AppendLine($"                using (var sqlConnection = new SqlConnection({table.Namespace}.Database.Access.Config.GetConnectionString()))");
            code.AppendLine("                {");
            code.AppendLine("                    sqlConnection.Open();");
            code.AppendLine($"                    var sqlCommand = new SqlCommand();");
            code.AppendLine($"                    sqlCommand.Connection = sqlConnection;");
            code.AppendLine();
            code.AppendLine("                    string queryIds = string.Empty;");
            code.AppendLine("                    for (int i = 0; i < ids.Count; i++)");
            code.AppendLine("                    {");
            code.AppendLine($"                        queryIds += \"@{primaryKeyColumnName}\" + i + \",\";");
            code.AppendLine($"                        sqlCommand.Parameters.AddWithValue(\"{primaryKeyColumnName}\" + i, ids[i]);");
            code.AppendLine("                    }");
            code.AppendLine("                    queryIds = queryIds.TrimEnd(',');");
            code.AppendLine();
            code.AppendLine($"                    sqlCommand.CommandText = $\"SELECT * FROM [{table.Name}] WHERE [{primaryKeyColumnName}] IN ({{queryIds}})\";");
            code.AppendLine($"                    new SqlDataAdapter(sqlCommand).Fill(dataTable);");
            code.AppendLine("                }");
            code.AppendLine();
            code.AppendLine("                if (dataTable.Rows.Count > 0)");
            code.AppendLine("                {");
            code.AppendLine($"                    return dataTable.Rows.Cast<DataRow>().Select(x => new {table.EntityClass}(x)).ToList();");
            code.AppendLine("                }");
            code.AppendLine("                else");
            code.AppendLine("                {");
            code.AppendLine($"                    return new List<{table.EntityClass}>();");
            code.AppendLine("                }");
            code.AppendLine("            }");
            code.AppendLine($"            return new List<{table.EntityClass}>();");
            code.AppendLine("        }");

            code.AppendLine($"        public List<{table.EntityClass}> GetMultiple(List<int> ids)");
            code.AppendLine("        {");
            code.AppendLine("            if (ids != null && ids.Count > 0)");
            code.AppendLine("            {");
            code.AppendLine($"                int maxQueryNumber = {table.Namespace}.Database.Access.Config.MAX_BATCH_SIZE;");
            code.AppendLine($"                List<{table.EntityClass}> results = null;");
            code.AppendLine("                if (ids.Count <= maxQueryNumber)");
            code.AppendLine("                {");
            code.AppendLine("                    results = getMultiple(ids);");
            code.AppendLine("                }");
            code.AppendLine("                else");
            code.AppendLine("                {");
            code.AppendLine($"                    int batchNumber = ids.Count / maxQueryNumber;");
            code.AppendLine($"                    results = new List<{table.EntityClass}>();");
            code.AppendLine("                    for (int i = 0; i < batchNumber; i++)");
            code.AppendLine("                    {");
            code.AppendLine("                        results.AddRange(getMultiple(ids.GetRange(i * maxQueryNumber, maxQueryNumber)));");
            code.AppendLine("                    }");
            code.AppendLine("                    results.AddRange(getMultiple(ids.GetRange(batchNumber * maxQueryNumber, ids.Count - batchNumber * maxQueryNumber)));");
            code.AppendLine("                }");
            code.AppendLine("                return results;");
            code.AppendLine("            }");
            code.AppendLine($"            return new List<{table.EntityClass}>();");
            code.AppendLine("        }");

            // Insert method
            code.AppendLine($"        public int Insert({table.EntityClass} item)");
            code.AppendLine("        {");
            code.AppendLine("            int response = int.MinValue;");
            code.AppendLine($"            using (var sqlConnection = new SqlConnection({table.Namespace}.Database.Access.Config.GetConnectionString()))");
            code.AppendLine("            {");
            code.AppendLine("                sqlConnection.Open();");
            code.AppendLine("                var sqlTransaction = sqlConnection.BeginTransaction();");
            code.AppendLine();

            // Check if Id is auto-incremented
            bool isAutoIncremented = table.Columns.Any(c => c.IsAutoIncrement);

            // Build the list of column names and values for the query
            List<string> columnNames = new List<string>();
            List<string> columnValues = new List<string>();
            foreach (var column in table.Columns.Where(c => !string.Equals(c.Name, primaryKeyColumnName, StringComparison.OrdinalIgnoreCase)))
            {
                columnNames.Add($"[{column.Name}]");
                columnValues.Add($"@{column.Name}");
            }

            // Build the INSERT query based on whether Id is auto-incremented
            string query;
            if (isAutoIncremented)
            {
                query = $"INSERT INTO [{table.Name}] ({string.Join(",", columnNames)}) OUTPUT INSERTED.[{primaryKeyColumnName}] VALUES ({string.Join(",", columnValues)}); SELECT SCOPE_IDENTITY();";
            }
            else
            {
                query = $"INSERT INTO [{table.Name}] ({string.Join(",", columnNames)}) VALUES ({string.Join(",", columnValues)}); SELECT SCOPE_IDENTITY();";
            }

            code.AppendLine($"                string query = \"{query}\";");
            code.AppendLine();
            code.AppendLine("                using (var sqlCommand = new SqlCommand(query, sqlConnection, sqlTransaction))");
            code.AppendLine("                {");

            // Add parameters for each column (excluding Id)
            foreach (var column in table.Columns.Where(c => !string.Equals(c.Name, primaryKeyColumnName, StringComparison.OrdinalIgnoreCase)))
            {
                code.AppendLine($"                    sqlCommand.Parameters.AddWithValue(\"{column.Name}\", item.{column.Name} == null ? (object)DBNull.Value : item.{column.Name});");
            }

            code.AppendLine();
            code.AppendLine("                    var result = sqlCommand.ExecuteScalar();");
            code.AppendLine("                    response = result == null || result == DBNull.Value ? int.MinValue : Convert.ToInt32(result);");
            code.AppendLine("                }");
            code.AppendLine();
            code.AppendLine("                sqlTransaction.Commit();");
            code.AppendLine();
            code.AppendLine("                return response;");
            code.AppendLine("            }");
            code.AppendLine("        }");

            // Insert multiple method
            code.AppendLine($"        private int insertMultiple(List<{table.EntityClass}> items)");
            code.AppendLine("        {");
            code.AppendLine("           if (items != null && items.Count > 0)");
            code.AppendLine("           {");
            code.AppendLine("               int results = -1;");
            code.AppendLine($"              using (var sqlConnection = new SqlConnection({table.Namespace}.Database.Access.Config.GetConnectionString()))");
            code.AppendLine("               {");
            code.AppendLine("                   sqlConnection.Open();");
            code.AppendLine("                   string query = \"\";");
            code.AppendLine("                   var sqlCommand = new SqlCommand(query, sqlConnection);");

            code.AppendLine("                   int i = 0;");
            code.AppendLine("                   foreach (var item in items)");
            code.AppendLine("                   {");
            code.AppendLine("                       i++;");
            code.AppendLine($"                      query += \" INSERT INTO [{table.Name}] ({string.Join(",", table.Columns.Where(c => !string.Equals(c.Name, primaryKeyColumnName, StringComparison.OrdinalIgnoreCase)).Select(c => $"[{c.Name}]"))}) VALUES (\"");

            // Add parameters for each column (excluding Id)
            int columnCount = table.Columns.Count(c => !string.Equals(c.Name, primaryKeyColumnName, StringComparison.OrdinalIgnoreCase));
            int currentColumn = 0;

            foreach (var column in table.Columns.Where(c => !string.Equals(c.Name, primaryKeyColumnName, StringComparison.OrdinalIgnoreCase)))
            {
                currentColumn++;
                code.AppendLine($"                          + \"@{column.Name}\" + i +");
                if (currentColumn < columnCount)
                {
                    code.AppendLine("                           \",\"");
                }
                else
                {
                    code.AppendLine("                           \"); \";");
                }
            }

            // Add parameters for each column (excluding Id)
            foreach (var column in table.Columns.Where(c => !string.Equals(c.Name, primaryKeyColumnName, StringComparison.OrdinalIgnoreCase)))
            {
                code.AppendLine($"                   sqlCommand.Parameters.AddWithValue(\"{column.Name}\" + i, item.{column.Name} == null ? (object)DBNull.Value : item.{column.Name});");
            }

            code.AppendLine("                     }");

            code.AppendLine("                   sqlCommand.CommandText = query;");
            code.AppendLine("                   results = sqlCommand.ExecuteNonQuery();");
            code.AppendLine("               }");

            code.AppendLine("               return results;");
            code.AppendLine("           }");

            code.AppendLine("           return -1;");
            code.AppendLine("       }");

            code.AppendLine($"        public int InsertMultiple(List<{table.EntityClass}> items)");
            code.AppendLine("        {");
            code.AppendLine("            if (items != null && items.Count > 0)");
            code.AppendLine("            {");
            code.AppendLine($"                int maxParamsNumber = {table.Namespace}.Database.Access.Config.MAX_BATCH_SIZE / {table.Columns.Count};");
            code.AppendLine("                int results = 0;");
            code.AppendLine("                if (items.Count <= maxParamsNumber)");
            code.AppendLine("                {");
            code.AppendLine("                    results = insertMultiple(items);");
            code.AppendLine("                }");
            code.AppendLine("                else");
            code.AppendLine("                {");
            code.AppendLine("                    int batchNumber = items.Count / maxParamsNumber;");
            code.AppendLine("                    for (int i = 0; i < batchNumber; i++)");
            code.AppendLine("                    {");
            code.AppendLine("                        results += insertMultiple(items.GetRange(i * maxParamsNumber, maxParamsNumber));");
            code.AppendLine("                    }");
            code.AppendLine("                    results += insertMultiple(items.GetRange(batchNumber * maxParamsNumber, items.Count - batchNumber * maxParamsNumber));");
            code.AppendLine("                }");
            code.AppendLine("                return results;");
            code.AppendLine("            }");
            code.AppendLine("            return -1;");
            code.AppendLine("        }");

            // Update method
            code.AppendLine($"        public int Update({table.EntityClass} item)");
            code.AppendLine("        {");
            code.AppendLine($"            using (var sqlConnection = new SqlConnection({table.Namespace}.Database.Access.Config.GetConnectionString()))");
            code.AppendLine("            {");
            code.AppendLine("                sqlConnection.Open();");
            code.AppendLine("                var sqlTransaction = sqlConnection.BeginTransaction();");
            code.AppendLine();
            code.AppendLine($"                string query = \"UPDATE [{table.Name}] SET {string.Join(", ", table.Columns.Where(c => !string.Equals(c.Name, primaryKeyColumnName, StringComparison.OrdinalIgnoreCase)).Select(c => $"[{c.Name}] = @{c.Name}"))} WHERE [{primaryKeyColumnName}] = @{primaryKeyColumnName}\";");
            code.AppendLine();
            code.AppendLine("                using (var sqlCommand = new SqlCommand(query, sqlConnection, sqlTransaction))");
            code.AppendLine("                {");
            code.AppendLine($"                    sqlCommand.Parameters.AddWithValue(\"{primaryKeyColumnName}\", item.{primaryKeyColumnName});");

            // Add parameters for each column (excluding Id)
            foreach (var column in table.Columns.Where(c => !string.Equals(c.Name, primaryKeyColumnName, StringComparison.OrdinalIgnoreCase)))
            {
                code.AppendLine($"                    sqlCommand.Parameters.AddWithValue(\"{column.Name}\", item.{column.Name} == null ? (object)DBNull.Value : item.{column.Name});");
            }

            code.AppendLine();
            code.AppendLine("                    int rowsAffected = sqlCommand.ExecuteNonQuery();");
            code.AppendLine("                    sqlTransaction.Commit();");
            code.AppendLine();
            code.AppendLine("                    return rowsAffected;");
            code.AppendLine("                }");
            code.AppendLine("            }");
            code.AppendLine("        }");

            // Update multiple method
            code.AppendLine($"        private int updateMultiple(List<{table.EntityClass}> items)");
            code.AppendLine("        {");
            code.AppendLine("            if (items != null && items.Count > 0)");
            code.AppendLine("            {");
            code.AppendLine("                int results = -1;");
            code.AppendLine($"                using (var sqlConnection = new SqlConnection({table.Namespace}.Database.Access.Config.GetConnectionString()))");
            code.AppendLine("                {");
            code.AppendLine("                    sqlConnection.Open();");
            code.AppendLine("                    string query = \"\";");
            code.AppendLine("                    var sqlCommand = new SqlCommand(query, sqlConnection);");
            code.AppendLine();
            code.AppendLine("                    int i = 0;");
            code.AppendLine("                    foreach (var item in items)");
            code.AppendLine("                    {");
            code.AppendLine("                        i++;");
            code.AppendLine($"                        query += \" UPDATE [{table.Name}] SET \"");

            currentColumn = 0;
            // Add the SET clauses for each column
            foreach (var column in table.Columns.Where(c => !string.Equals(c.Name, primaryKeyColumnName, StringComparison.OrdinalIgnoreCase)))
            {

                currentColumn++;
                code.AppendLine($"                          + \"[{column.Name}]=@{column.Name}\" + i +");
                if (currentColumn < columnCount)
                {
                    code.AppendLine("                           \",\"");
                }
                else
                {
                    code.AppendLine($"                         \" WHERE [{primaryKeyColumnName}]=@{primaryKeyColumnName}\" + i");
                    code.AppendLine($"                            + \"; \";");
                }
            }



            // Add parameters for each column (excluding Id)
            foreach (var column in table.Columns.Where(c => !string.Equals(c.Name, primaryKeyColumnName, StringComparison.OrdinalIgnoreCase)))
            {
                code.AppendLine($"                        sqlCommand.Parameters.AddWithValue(\"{column.Name}\" + i, item.{column.Name} == null ? (object)DBNull.Value : item.{column.Name});");
            }

            // Add parameter for Id
            code.AppendLine($"                        sqlCommand.Parameters.AddWithValue(\"{primaryKeyColumnName}\" + i, item.{primaryKeyColumnName});");
            code.AppendLine("                    }");
            code.AppendLine();
            code.AppendLine("                    sqlCommand.CommandText = query;");
            code.AppendLine();
            code.AppendLine("                    results = sqlCommand.ExecuteNonQuery();");
            code.AppendLine("                }");
            code.AppendLine();
            code.AppendLine("                return results;");
            code.AppendLine("            }");
            code.AppendLine();
            code.AppendLine("            return -1;");
            code.AppendLine("        }");

            code.AppendLine($"        public int UpdateMultiple(List<{table.EntityClass}> items)");
            code.AppendLine("        {");
            code.AppendLine("            if (items != null && items.Count > 0)");
            code.AppendLine("            {");
            code.AppendLine($"                int maxParamsNumber = {table.Namespace}.Database.Access.Config.MAX_BATCH_SIZE / {table.Columns.Count};");
            code.AppendLine("                int results = 0;");
            code.AppendLine();
            code.AppendLine("                if (items.Count <= maxParamsNumber)");
            code.AppendLine("                {");
            code.AppendLine("                    results = updateMultiple(items);");
            code.AppendLine("                }");
            code.AppendLine("                else");
            code.AppendLine("                {");
            code.AppendLine("                    int batchNumber = items.Count / maxParamsNumber;");
            code.AppendLine("                    for (int i = 0; i < batchNumber; i++)");
            code.AppendLine("                    {");
            code.AppendLine("                        results += updateMultiple(items.GetRange(i * maxParamsNumber, maxParamsNumber));");
            code.AppendLine("                    }");
            code.AppendLine("                    results += updateMultiple(items.GetRange(batchNumber * maxParamsNumber, items.Count - batchNumber * maxParamsNumber));");
            code.AppendLine("                }");
            code.AppendLine();
            code.AppendLine("                return results;");
            code.AppendLine("            }");
            code.AppendLine();
            code.AppendLine("            return -1;");
            code.AppendLine("        }");

            // Delete method
            code.AppendLine($"        public int Delete(int id)");
            code.AppendLine("        {");
            code.AppendLine($"            using (var sqlConnection = new SqlConnection({table.Namespace}.Database.Access.Config.GetConnectionString()))");
            code.AppendLine("            {");
            code.AppendLine("                sqlConnection.Open();");
            code.AppendLine("                var sqlTransaction = sqlConnection.BeginTransaction();");
            code.AppendLine();
            code.AppendLine($"                string query = \"DELETE FROM [{table.Name}] WHERE [{primaryKeyColumnName}] = @{primaryKeyColumnName}\";");
            code.AppendLine();
            code.AppendLine("                using (var sqlCommand = new SqlCommand(query, sqlConnection, sqlTransaction))");
            code.AppendLine("                {");
            code.AppendLine($"                    sqlCommand.Parameters.AddWithValue(\"{primaryKeyColumnName}\", id);");
            code.AppendLine();
            code.AppendLine("                    int rowsAffected = sqlCommand.ExecuteNonQuery();");
            code.AppendLine("                    sqlTransaction.Commit();");
            code.AppendLine();
            code.AppendLine("                    return rowsAffected;");
            code.AppendLine("                }");
            code.AppendLine("            }");
            code.AppendLine("        }");

            // Delete multiple method
            code.AppendLine($"        private int deleteMultiple(List<int> ids)");
            code.AppendLine("        {");
            code.AppendLine($"            using (var sqlConnection = new SqlConnection({table.Namespace}.Database.Access.Config.GetConnectionString()))");
            code.AppendLine("            {");
            code.AppendLine("                sqlConnection.Open();");
            code.AppendLine("                var sqlTransaction = sqlConnection.BeginTransaction();");
            code.AppendLine();
            code.AppendLine($"                string queryIds = string.Join(\",\", Enumerable.Range(0, ids.Count).Select(i => \"@{primaryKeyColumnName}\" + i));");
            code.AppendLine($"                string query = \"DELETE FROM [{table.Name}] WHERE [{primaryKeyColumnName}] IN (\" + queryIds + \")\";");
            code.AppendLine();
            code.AppendLine("                using (var sqlCommand = new SqlCommand(query, sqlConnection, sqlTransaction))");
            code.AppendLine("                {");
            code.AppendLine("                    for (int i = 0; i < ids.Count; i++)");
            code.AppendLine("                    {");
            code.AppendLine($"                        sqlCommand.Parameters.AddWithValue(\"{primaryKeyColumnName}\" + i, ids[i]);");
            code.AppendLine("                    }");
            code.AppendLine();
            code.AppendLine("                    int rowsAffected = sqlCommand.ExecuteNonQuery();");
            code.AppendLine("                    sqlTransaction.Commit();");
            code.AppendLine();
            code.AppendLine("                    return rowsAffected;");
            code.AppendLine("                }");
            code.AppendLine("            }");
            code.AppendLine("        }");


            code.AppendLine($"        public int DeleteMultiple(List<int> ids)");
            code.AppendLine("         {");
            code.AppendLine("           if (ids != null && ids.Count > 0)");
            code.AppendLine("           {");
            code.AppendLine($"              int maxParamsNumber = {table.Namespace}.Database.Access.Config.MAX_BATCH_SIZE;");
            code.AppendLine("               int results = 0;");
            code.AppendLine();
            code.AppendLine("               if (ids.Count <= maxParamsNumber)");
            code.AppendLine("               {");
            code.AppendLine("                   results = deleteMultiple(ids);");
            code.AppendLine("               }");
            code.AppendLine("               else");
            code.AppendLine("               {");
            code.AppendLine("                   int batchNumber = ids.Count / maxParamsNumber;");
            code.AppendLine("                   for (int i = 0; i < batchNumber; i++)");
            code.AppendLine("                   {");
            code.AppendLine("                       results += deleteMultiple(ids.GetRange(i * maxParamsNumber, maxParamsNumber));");
            code.AppendLine("                   }");
            code.AppendLine("                   results += deleteMultiple(ids.GetRange(batchNumber * maxParamsNumber, ids.Count - batchNumber * maxParamsNumber));");
            code.AppendLine("               }");
            code.AppendLine();
            code.AppendLine("               return results;");
            code.AppendLine("           }");
            code.AppendLine("           else");
            code.AppendLine("           {");
            code.AppendLine("               return -1;");
            code.AppendLine("           }");
            code.AppendLine("        }");


            code.AppendLine("        #region Transaction Methods");

            // Get With Transaction method
            code.AppendLine($"        public {table.EntityClass} GetWithTransaction(int id, SqlConnection connection, SqlTransaction transaction)");
            code.AppendLine("        {");
            code.AppendLine("            var dataTable = new DataTable();");
            code.AppendLine();
            code.AppendLine($"            string query = \"SELECT * FROM [{table.Name}] WHERE [{primaryKeyColumnName}]=@{primaryKeyColumnName}\";");
            code.AppendLine($"            var sqlCommand = new SqlCommand(query, connection, transaction);");
            code.AppendLine($"            sqlCommand.Parameters.AddWithValue(\"{primaryKeyColumnName}\", id);");
            code.AppendLine();
            code.AppendLine("            new SqlDataAdapter(sqlCommand).Fill(dataTable);");
            code.AppendLine();
            code.AppendLine("            if (dataTable.Rows.Count > 0)");
            code.AppendLine($"                return new {table.EntityClass}(dataTable.Rows[0]);");
            code.AppendLine("            else");
            code.AppendLine("                return null;");
            code.AppendLine("        }");

            // Get all With Transaction methods
            code.AppendLine($"        public List<{table.EntityClass}> GetAllWithTransaction(SqlConnection connection, SqlTransaction transaction)");
            code.AppendLine("        {");
            code.AppendLine("            var dataTable = new DataTable();");
            code.AppendLine();
            code.AppendLine($"            string query = \"SELECT * FROM [{table.Name}]\";");
            code.AppendLine($"            var sqlCommand = new SqlCommand(query, connection, transaction);");
            code.AppendLine();
            code.AppendLine("            new SqlDataAdapter(sqlCommand).Fill(dataTable);");
            code.AppendLine();
            code.AppendLine("            if (dataTable.Rows.Count > 0)");
            code.AppendLine($"                return dataTable.Rows.Cast<DataRow>().Select(x => new {table.EntityClass}(x)).ToList();");
            code.AppendLine("            else");
            code.AppendLine($"                return new List<{table.EntityClass}>();");
            code.AppendLine("        }");

            // Get multiple With Transaction method

            code.AppendLine($"        private List<{table.EntityClass}> getMultipleWithTransaction(List<int> ids, SqlConnection connection, SqlTransaction transaction)");
            code.AppendLine("        {");
            code.AppendLine("            if (ids != null && ids.Count > 0)");
            code.AppendLine("            {");
            code.AppendLine("                var dataTable = new DataTable();");
            code.AppendLine();
            code.AppendLine($"                var sqlCommand = new SqlCommand(\"\", connection, transaction);");
            code.AppendLine("                string queryIds = string.Empty;");
            code.AppendLine();
            code.AppendLine("                for (int i = 0; i < ids.Count; i++)");
            code.AppendLine("                {");
            code.AppendLine($"                    queryIds += \"@{primaryKeyColumnName}\" + i + \",\";");
            code.AppendLine($"                    sqlCommand.Parameters.AddWithValue(\"{primaryKeyColumnName}\" + i, ids[i]);");
            code.AppendLine("                }");
            code.AppendLine("                queryIds = queryIds.TrimEnd(',');");
            code.AppendLine();
            code.AppendLine($"                sqlCommand.CommandText = \"SELECT * FROM [{table.Name}] WHERE [{primaryKeyColumnName}] IN (\"+ queryIds +\")\";");
            code.AppendLine("                new SqlDataAdapter(sqlCommand).Fill(dataTable);");
            code.AppendLine();
            code.AppendLine("                if (dataTable.Rows.Count > 0)");
            code.AppendLine($"                    return dataTable.Rows.Cast<DataRow>().Select(x => new {table.EntityClass}(x)).ToList();");
            code.AppendLine("                else");
            code.AppendLine($"                    return new List<{table.EntityClass}>();");
            code.AppendLine("            }");
            code.AppendLine($"            return new List<{table.EntityClass}>();");
            code.AppendLine("        }");

            code.AppendLine($"        public List<{table.EntityClass}> GetMultipleWithTransaction(List<int> ids, SqlConnection connection, SqlTransaction transaction)");
            code.AppendLine("        {");
            code.AppendLine("            if (ids != null && ids.Count > 0)");
            code.AppendLine("            {");
            code.AppendLine($"                int maxQueryNumber = {table.Namespace}.Database.Access.Config.MAX_BATCH_SIZE;");
            code.AppendLine($"                List<{table.EntityClass}> results = null;");
            code.AppendLine();
            code.AppendLine("                if (ids.Count <= maxQueryNumber)");
            code.AppendLine("                {");
            code.AppendLine($"                    results = getMultipleWithTransaction(ids, connection, transaction);");
            code.AppendLine("                }");
            code.AppendLine("                else");
            code.AppendLine("                {");
            code.AppendLine("                    int batchNumber = ids.Count / maxQueryNumber;");
            code.AppendLine($"                    results = new List<{table.EntityClass}>();");
            code.AppendLine();
            code.AppendLine("                    for (int i = 0; i < batchNumber; i++)");
            code.AppendLine("                    {");
            code.AppendLine($"                        results.AddRange(getMultipleWithTransaction(ids.GetRange(i * maxQueryNumber, maxQueryNumber), connection, transaction));");
            code.AppendLine("                    }");
            code.AppendLine();
            code.AppendLine($"                    results.AddRange(getMultipleWithTransaction(ids.GetRange(batchNumber * maxQueryNumber, ids.Count - batchNumber * maxQueryNumber), connection, transaction));");
            code.AppendLine("                }");
            code.AppendLine();
            code.AppendLine("                return results;");
            code.AppendLine("            }");
            code.AppendLine($"            return new List<{table.EntityClass}>();");
            code.AppendLine("        }");

            // InsertWithTransaction methods
            code.AppendLine($"        public int InsertWithTransaction({table.EntityClass} item, SqlConnection connection, SqlTransaction transaction)");
            code.AppendLine("        {");
            code.AppendLine($"            int response = int.MinValue;");
            code.AppendLine($"            string query = \"INSERT INTO [{table.Name}] ({string.Join(",", table.Columns.Where(c => !string.Equals(c.Name, primaryKeyColumnName, StringComparison.OrdinalIgnoreCase)).Select(c => $"[{c.Name}]"))}) OUTPUT INSERTED.[{primaryKeyColumnName}] VALUES ({string.Join(",", table.Columns.Where(c => !string.Equals(c.Name, primaryKeyColumnName, StringComparison.OrdinalIgnoreCase)).Select(c => $"@{c.Name}"))}); SELECT SCOPE_IDENTITY();\";");
            code.AppendLine($"            using (var sqlCommand = new SqlCommand(query, connection, transaction))");
            code.AppendLine("            {");


            // Adding parameters without the GenerateParametersAddWithValue method
            foreach (var column in table.Columns.Where(c => !string.Equals(c.Name, primaryKeyColumnName, StringComparison.OrdinalIgnoreCase)))
            {
                code.AppendLine($"                sqlCommand.Parameters.AddWithValue(\"{column.Name}\", item.{column.Name} == null ? (object)DBNull.Value : item.{column.Name});");
            }

            code.AppendLine();
            code.AppendLine("                var result = sqlCommand.ExecuteScalar();");
            code.AppendLine("                response = result == null || result == DBNull.Value ? int.MinValue : Convert.ToInt32(result);");
            code.AppendLine("            }");
            code.AppendLine();
            code.AppendLine("            return response;");
            code.AppendLine("        }");

            // Insert multiple With Transaction method
            code.AppendLine($"        private int insertMultipleWithTransaction(List<{table.EntityClass}> items, SqlConnection connection, SqlTransaction transaction)");
            code.AppendLine("        {");
            code.AppendLine("            if (items != null && items.Count > 0)");
            code.AppendLine("            {");
            code.AppendLine($"                int results = -1;");
            code.AppendLine($"                string query = \"\";");
            code.AppendLine($"                using (var sqlCommand = new SqlCommand(query, connection, transaction))");
            code.AppendLine("                {");
            code.AppendLine($"                    int i = 0;");
            code.AppendLine($"                    foreach (var item in items)");
            code.AppendLine($"                    {{");
            code.AppendLine($"                        i++;");
            code.AppendLine($"                      query += \" INSERT INTO [{table.Name}] ({string.Join(",", table.Columns.Where(c => !string.Equals(c.Name, primaryKeyColumnName, StringComparison.OrdinalIgnoreCase)).Select(c => $"[{c.Name}]"))}) VALUES (\"");


            // Add parameters for each column (excluding Id)
            currentColumn = 0;

            foreach (var column in table.Columns.Where(c => !string.Equals(c.Name, primaryKeyColumnName, StringComparison.OrdinalIgnoreCase)))
            {
                currentColumn++;
                code.AppendLine($"                          + \"@{column.Name}\" + i +");
                if (currentColumn < columnCount)
                {
                    code.AppendLine("                           \",\"");
                }
                else
                {
                    code.AppendLine("                           \"); \";");
                }
            }

            // Adding parameters without the GenerateParametersAddWithValue method
            foreach (var column in table.Columns.Where(c => !string.Equals(c.Name, primaryKeyColumnName, StringComparison.OrdinalIgnoreCase)))
            {
                code.AppendLine($"                   sqlCommand.Parameters.AddWithValue(\"{column.Name}\" + i, item.{column.Name} == null ? (object)DBNull.Value : item.{column.Name});");
            }

            code.AppendLine($"                    }}");
            code.AppendLine($"                    sqlCommand.CommandText = query;");
            code.AppendLine($"                    results = sqlCommand.ExecuteNonQuery();");
            code.AppendLine("                }");
            code.AppendLine($"                return results;");
            code.AppendLine("            }");
            code.AppendLine("            return -1;");
            code.AppendLine("        }");

            code.AppendLine($"        public int InsertMultipleWithTransaction(List<{table.EntityClass}> items, SqlConnection connection, SqlTransaction transaction)");
            code.AppendLine("        {");
            code.AppendLine("            if (items != null && items.Count > 0)");
            code.AppendLine("            {");
            code.AppendLine($"                int maxParamsNumber = {table.Namespace}.Database.Access.Config.MAX_BATCH_SIZE / 4; // Nb params per query");
            code.AppendLine($"                int results = 0;");
            code.AppendLine("                if (items.Count <= maxParamsNumber)");
            code.AppendLine("                {");
            code.AppendLine($"                    results = insertMultipleWithTransaction(items, connection, transaction);");
            code.AppendLine("                }");
            code.AppendLine("                else");
            code.AppendLine("                {");
            code.AppendLine("                    int batchNumber = items.Count / maxParamsNumber;");
            code.AppendLine("                    results = 0;");
            code.AppendLine("                    for (int i = 0; i < batchNumber; i++)");
            code.AppendLine("                    {");
            code.AppendLine($"                        results += insertMultipleWithTransaction(items.GetRange(i * maxParamsNumber, maxParamsNumber), connection, transaction);");
            code.AppendLine("                    }");
            code.AppendLine($"                    results += insertMultipleWithTransaction(items.GetRange(batchNumber * maxParamsNumber, items.Count - batchNumber * maxParamsNumber), connection, transaction);");
            code.AppendLine("                }");
            code.AppendLine($"                return results;");
            code.AppendLine("            }");
            code.AppendLine("            return -1;");
            code.AppendLine("        }");



            // Update With Transaction method
            code.AppendLine($"        public int UpdateWithTransaction({table.EntityClass} item, SqlConnection connection, SqlTransaction transaction)");
            code.AppendLine("        {");
            code.AppendLine($"            int results = -1;");
            code.AppendLine($"            string query = \"UPDATE [{table.Name}] SET {string.Join(", ", table.Columns.Where(c => !string.Equals(c.Name, primaryKeyColumnName, StringComparison.OrdinalIgnoreCase)).Select(c => $"[{c.Name}] = @{c.Name}"))} WHERE [{primaryKeyColumnName}] = @{primaryKeyColumnName}\";");
            code.AppendLine($"            var sqlCommand = new SqlCommand(query, connection, transaction);");
            code.AppendLine($"            sqlCommand.Parameters.AddWithValue(\"{primaryKeyColumnName}\", item.{primaryKeyColumnName});");

            // Adding parameters without the GenerateParametersAddWithValue method
            foreach (var column in table.Columns.Where(c => !string.Equals(c.Name, primaryKeyColumnName, StringComparison.OrdinalIgnoreCase)))
            {
                code.AppendLine($"            sqlCommand.Parameters.AddWithValue(\"{column.Name}\", item.{column.Name} == null ? (object)DBNull.Value : item.{column.Name});");
            }

            code.AppendLine($"            results = sqlCommand.ExecuteNonQuery();");
            code.AppendLine($"            return results;");
            code.AppendLine("        }");

            // Update multiple With Transaction method
            code.AppendLine($"        private int updateMultipleWithTransaction(List<{table.EntityClass}> items, SqlConnection connection, SqlTransaction transaction)");
            code.AppendLine("        {");
            code.AppendLine("            if (items != null && items.Count > 0)");
            code.AppendLine("            {");
            code.AppendLine($"                int results = -1;");
            code.AppendLine($"                string query = \"\";");
            code.AppendLine($"                var sqlCommand = new SqlCommand(query, connection, transaction);");
            code.AppendLine($"                int i = 0;");
            code.AppendLine($"                foreach (var item in items)");
            code.AppendLine($"                {{");
            code.AppendLine($"                    i++;");
            code.AppendLine($"                        query += \" UPDATE [{table.Name}] SET \"");

            currentColumn = 0;
            // Add the SET clauses for each column
            foreach (var column in table.Columns.Where(c => !string.Equals(c.Name, primaryKeyColumnName, StringComparison.OrdinalIgnoreCase)))
            {

                currentColumn++;
                code.AppendLine($"                          + \"[{column.Name}]=@{column.Name}\" + i +");
                if (currentColumn < columnCount)
                {
                    code.AppendLine("                           \",\"");
                }
                else
                {
                    code.AppendLine($"                         \" WHERE [{primaryKeyColumnName}]=@{primaryKeyColumnName}\" + i");
                    code.AppendLine($"                            + \"; \";");
                }
            }

            // Adding parameters without the GenerateParametersAddWithValue method
            foreach (var column in table.Columns.Where(c => !string.Equals(c.Name, primaryKeyColumnName, StringComparison.OrdinalIgnoreCase)))
            {
                code.AppendLine($"                        sqlCommand.Parameters.AddWithValue(\"{column.Name}\" + i, item.{column.Name} == null ? (object)DBNull.Value : item.{column.Name});");
            }
            // Add parameter for Id
            code.AppendLine($"                        sqlCommand.Parameters.AddWithValue(\"{primaryKeyColumnName}\" + i, item.{primaryKeyColumnName});");

            code.AppendLine($"                }}");
            code.AppendLine($"                sqlCommand.CommandText = query;");
            code.AppendLine($"                return sqlCommand.ExecuteNonQuery();");
            code.AppendLine("            }");
            code.AppendLine("            return -1;");
            code.AppendLine("        }");

            code.AppendLine($"        public int UpdateMultipleWithTransaction(List<{table.EntityClass}> items, SqlConnection connection, SqlTransaction transaction)");
            code.AppendLine("        {");
            code.AppendLine("            if (items != null && items.Count > 0)");
            code.AppendLine("            {");
            code.AppendLine($"                int maxParamsNumber = {table.Namespace}.Database.Access.Config.MAX_BATCH_SIZE / 4; // Nb params per query");
            code.AppendLine($"                int results = 0;");
            code.AppendLine("                if (items.Count <= maxParamsNumber)");
            code.AppendLine("                {");
            code.AppendLine($"                    results = updateMultipleWithTransaction(items, connection, transaction);");
            code.AppendLine("                }");
            code.AppendLine("                else");
            code.AppendLine("                {");
            code.AppendLine("                    int batchNumber = items.Count / maxParamsNumber;");
            code.AppendLine("                    results = 0;");
            code.AppendLine("                    for (int i = 0; i < batchNumber; i++)");
            code.AppendLine("                    {");
            code.AppendLine($"                        results += updateMultipleWithTransaction(items.GetRange(i * maxParamsNumber, maxParamsNumber), connection, transaction);");
            code.AppendLine("                    }");
            code.AppendLine($"                    results += updateMultipleWithTransaction(items.GetRange(batchNumber * maxParamsNumber, items.Count - batchNumber * maxParamsNumber), connection, transaction);");
            code.AppendLine("                }");
            code.AppendLine($"                return results;");
            code.AppendLine("            }");
            code.AppendLine("            return -1;");
            code.AppendLine("        }");

            // Delete With Transaction method
            code.AppendLine($"        public int DeleteWithTransaction(int id, SqlConnection connection, SqlTransaction transaction)");
            code.AppendLine("        {");
            code.AppendLine($"            int results = -1;");
            code.AppendLine($"            string query = \"DELETE FROM [{table.Name}] WHERE [{primaryKeyColumnName}] = @{primaryKeyColumnName}\";");
            code.AppendLine($"            var sqlCommand = new SqlCommand(query, connection, transaction);");
            code.AppendLine($"            sqlCommand.Parameters.AddWithValue(\"{primaryKeyColumnName}\", id);");
            code.AppendLine($"            results = sqlCommand.ExecuteNonQuery();");
            code.AppendLine($"            return results;");
            code.AppendLine("        }");

            // Delete multiple With Transaction method

            code.AppendLine($"        private int deleteMultipleWithTransaction(List<int> ids, SqlConnection connection, SqlTransaction transaction)");
            code.AppendLine("        {");
            code.AppendLine("            if (ids != null && ids.Count > 0)");
            code.AppendLine("            {");
            code.AppendLine($"                int results = -1;");
            code.AppendLine($"                var sqlCommand = new SqlCommand(\"\", connection, transaction);");
            code.AppendLine($"                string queryIds = string.Join(\",\", ids.Select((id, i) => \"@{primaryKeyColumnName}\" + i));");
            code.AppendLine($"                sqlCommand.CommandText = $\"DELETE FROM [{table.Name}] WHERE [{primaryKeyColumnName}] IN (\" + queryIds +\")\";");

            code.AppendLine("                 for (int i = 0; i < ids.Count; i++)");
            code.AppendLine("                    {");
            code.AppendLine($"                sqlCommand.Parameters.AddWithValue(\"{primaryKeyColumnName}\" + i , ids[i]);");
            code.AppendLine("                    }");

            code.AppendLine($"                results = sqlCommand.ExecuteNonQuery();");
            code.AppendLine($"                return results;");
            code.AppendLine("            }");
            code.AppendLine("            return -1;");
            code.AppendLine("        }");

            code.AppendLine($"        public int DeleteMultipleWithTransaction(List<int> ids, SqlConnection connection, SqlTransaction transaction)");
            code.AppendLine("        {");
            code.AppendLine("            if (ids != null && ids.Count > 0)");
            code.AppendLine("            {");
            code.AppendLine($"                int maxParamsNumber = {table.Namespace}.Database.Access.Config.MAX_BATCH_SIZE;");
            code.AppendLine($"                int results = 0;");
            code.AppendLine("                if (ids.Count <= maxParamsNumber)");
            code.AppendLine("                {");
            code.AppendLine($"                    results = deleteMultipleWithTransaction(ids, connection, transaction);");
            code.AppendLine("                }");
            code.AppendLine("                else");
            code.AppendLine("                {");
            code.AppendLine("                    int batchNumber = ids.Count / maxParamsNumber;");
            code.AppendLine("                    results = 0;");
            code.AppendLine("                    for (int i = 0; i < batchNumber; i++)");
            code.AppendLine("                    {");
            code.AppendLine($"                        results += deleteMultipleWithTransaction(ids.GetRange(i * maxParamsNumber, maxParamsNumber), connection, transaction);");
            code.AppendLine("                    }");
            code.AppendLine($"                    results += deleteMultipleWithTransaction(ids.GetRange(batchNumber * maxParamsNumber, ids.Count - batchNumber * maxParamsNumber), connection, transaction);");
            code.AppendLine("                }");
            code.AppendLine($"                return results;");
            code.AppendLine("            }");
            code.AppendLine("            return -1;");
            code.AppendLine("        }");

            code.AppendLine("        #endregion Transaction Methods");
            code.AppendLine("        #endregion Default Methods");
            code.AppendLine();

            // Custom Methods region
            code.AppendLine("        #region Custom Methods");



            code.AppendLine("        #endregion Custom Methods");
            code.AppendLine("    }");
            code.AppendLine("}");

            return code.ToString();
        }

        private string GenerateServiceInterfaceCode(Table table, string database)
        {
            var serviceInterfaceCode = $"using System.Collections.Generic;\n";
            serviceInterfaceCode += $"using System.Threading.Tasks;\n\n";
            serviceInterfaceCode += $"using System.Data.SqlClient;\n\n";
            serviceInterfaceCode += $"using Microsoft.Data.SqlClient;\n\n";
            serviceInterfaceCode += $"using {table.Namespace}.Database.Entities.{database};\n\n";
            serviceInterfaceCode += $"namespace {table.Namespace}.Services.{database}.{table.Name}\n\n";
            serviceInterfaceCode += $"{{\n";
            serviceInterfaceCode += $"public interface I{table.Name}Service\n";
            serviceInterfaceCode += $"{{\n";
            serviceInterfaceCode += $"    List<{table.Namespace}.Database.Entities.{database}.{table.Name}> GetAll();\n";
            serviceInterfaceCode += $"    {table.Namespace}.Database.Entities.{database}.{table.Name} GetById(int id);\n";
            serviceInterfaceCode += $"    List<{table.Namespace}.Database.Entities.{database}.{table.Name}> GetMultiple(List<int> ids);\n";
            serviceInterfaceCode += $"    int Insert({table.Namespace}.Database.Entities.{database}.{table.Name} entity);\n";
            serviceInterfaceCode += $"    int InsertMultiple(List<{table.Namespace}.Database.Entities.{database}.{table.Name}> items);\n";
            serviceInterfaceCode += $"    int Update({table.Namespace}.Database.Entities.{database}.{table.Name} entity);\n";
            serviceInterfaceCode += $"    int UpdateMultiple(List<{table.Namespace}.Database.Entities.{database}.{table.Name}> items);\n";
            serviceInterfaceCode += $"    int Delete(int id);\n";
            serviceInterfaceCode += $"    int DeleteMultiple(List<int> ids);\n";
            serviceInterfaceCode += $"}}\n";
            serviceInterfaceCode += $"}}\n";

            return serviceInterfaceCode;
        }

        private string GenerateServiceClassCode(Table table, string database)
        {
            var serviceClassCode = $"using System.Collections.Generic;\n";
            serviceClassCode += $"using System.Threading.Tasks;\n\n";
            serviceClassCode += $"using System.Data.SqlClient;\n\n";
            serviceClassCode += $"using Microsoft.Data.SqlClient;\n\n";
            serviceClassCode += $"using {table.Namespace}.Database.Entities.{database};\n\n";
            serviceClassCode += $"using {table.Namespace}.Database.Access.{database};\n\n";
            serviceClassCode += $"namespace {table.Namespace}.Services.{database}.{table.Name}\n\n";
            serviceClassCode += $"{{\n";
            serviceClassCode += $"public class {table.Name}Service : I{table.Name}Service\n";
            serviceClassCode += $"{{\n";
            serviceClassCode += $"    private readonly {table.Namespace}.Database.Access.{database}.{table.Name}Access _access;\n\n";
            serviceClassCode += $"    public {table.Name}Service({table.Name}Access access)\n";
            serviceClassCode += $"    {{\n";
            serviceClassCode += $"        _access = access;\n";
            serviceClassCode += $"    }}\n\n";
            serviceClassCode += $"    public List<{table.Namespace}.Database.Entities.{database}.{table.Name}> GetAll()\n";
            serviceClassCode += $"    {{\n";
            serviceClassCode += $"        return _access.GetAll();\n";
            serviceClassCode += $"    }}\n\n";
            serviceClassCode += $"    public {table.Namespace}.Database.Entities.{database}.{table.Name} GetById(int id)\n";
            serviceClassCode += $"    {{\n";
            serviceClassCode += $"        return _access.Get(id);\n";
            serviceClassCode += $"    }}\n\n";
            serviceClassCode += $"    public List<{table.Namespace}.Database.Entities.{database}.{table.Name}> GetMultiple(List<int> ids)\n";
            serviceClassCode += $"    {{\n";
            serviceClassCode += $"        return _access.GetMultiple(ids);\n";
            serviceClassCode += $"    }}\n\n";
            serviceClassCode += $"    public int Insert({table.Namespace}.Database.Entities.{database}.{table.Name} entity)\n";
            serviceClassCode += $"    {{\n";
            serviceClassCode += $"        return _access.Insert(entity);\n";
            serviceClassCode += $"    }}\n\n";
            serviceClassCode += $"    public int InsertMultiple(List<{table.Namespace}.Database.Entities.{database}.{table.Name}> items)\n";
            serviceClassCode += $"    {{\n";
            serviceClassCode += $"        return _access.InsertMultiple(items);\n";
            serviceClassCode += $"    }}\n\n";
            serviceClassCode += $"    public int Update({table.Namespace}.Database.Entities.{database}.{table.Name} entity)\n";
            serviceClassCode += $"    {{\n";
            serviceClassCode += $"        return _access.Update(entity);\n";
            serviceClassCode += $"    }}\n\n";
            serviceClassCode += $"    public int UpdateMultiple(List<{table.Namespace}.Database.Entities.{database}.{table.Name}> items)\n";
            serviceClassCode += $"    {{\n";
            serviceClassCode += $"        return _access.UpdateMultiple(items);\n";
            serviceClassCode += $"    }}\n\n";
            serviceClassCode += $"    public int Delete(int id)\n";
            serviceClassCode += $"    {{\n";
            serviceClassCode += $"        return _access.Delete(id);\n";
            serviceClassCode += $"    }}\n";
            serviceClassCode += $"    public int DeleteMultiple(List<int> ids)\n";
            serviceClassCode += $"    {{\n";
            serviceClassCode += $"        return _access.DeleteMultiple(ids);\n";
            serviceClassCode += $"    }}\n";
            serviceClassCode += $"}}\n";
            serviceClassCode += $"}}\n";

            return serviceClassCode;
        }

        private string GenerateControllerCode(Table table, string database)
        {
            var controllerCode = $"using Microsoft.AspNetCore.Mvc;\n";
            controllerCode += $"using System.Threading.Tasks;\n";
            controllerCode += $"using System.Data.SqlClient;\n\n";
            controllerCode += $"using Microsoft.Data.SqlClient;\n\n";
            controllerCode += $"using {table.Namespace}.Services.{database}.{table.Name};\n";
            controllerCode += $"using {table.Namespace}.Database.Entities.{database};\n";
            controllerCode += "using System.ComponentModel.DataAnnotations;\n";
            controllerCode += $"[ApiController]\n";
            controllerCode += $"[Route(\"[controller]/[action]\")]\n";
            controllerCode += $"public class {table.Name}Controller : ControllerBase\n";
            controllerCode += $"{{\n";
            controllerCode += $"    private const string MODULE = \"{table.Name}\";\n";
            controllerCode += $"    private readonly ILogger<{table.Name}Controller> _logger;\n";
            controllerCode += $"    private I{table.Name}Service _{table.Name.ToLower()}Service;\n\n";
            controllerCode += $"    public {table.Name}Controller(ILogger<{table.Name}Controller> logger, I{table.Name}Service {table.Name.ToLower()}Service)\n";
            controllerCode += $"    {{\n";
            controllerCode += $"        _logger = logger;\n";
            controllerCode += $"        _{table.Name.ToLower()}Service = {table.Name.ToLower()}Service;\n";
            controllerCode += $"    }}\n\n";

            // Generate actions
            foreach (var action in GetCrudActions())
            {
                controllerCode += GenerateActionMethod(action, table);
            }

            controllerCode += $"}}\n";
            return controllerCode;
        }

        private string GenerateActionMethod(string action, Table table)
        {
            var methodName = GetActionMethodName(action);
            var parameterType = GetActionParameterType(action, table);

            // Use appropriate HTTP method attributes
            string httpMethodAttribute = action switch
            {
                "GetAll" => $"      [HttpGet(Name = \"{table.Name}{methodName}\")]\n",
                "GetById" => $"     [HttpGet(Name = \"{table.Name}{methodName}\")]\n",
                "GetMultiple" => $"     [HttpGet(Name = \"{table.Name}{methodName}\")]\n",
                "Insert" => $"      [HttpPost(Name = \"{table.Name}{methodName}\")]\n",
                "InsertMultiple" => $"      [HttpPost(Name = \"{table.Name}{methodName}\")]\n",
                "Update" => $"      [HttpPut(Name = \"{table.Name}{methodName}\")]\n",
                "UpdateMultiple" => $"      [HttpPut(Name = \"{table.Name}{methodName}\")]\n",
                "Delete" => $"      [HttpDelete(Name = \"{table.Name}{methodName}\")]\n",
                "DeleteMultiple" => $"      [HttpDelete(Name = \"{table.Name}{methodName}\")]\n",
                _ => throw new NotSupportedException($"Unsupported action: {action}")
            };

            var actionCode = $"{httpMethodAttribute}";
            actionCode += $"    [ProducesResponseType(typeof({parameterType}), 200)]\n";

            // Modify the method signature based on the action
            if (action == "GetAll")
            {
                actionCode += $"    public IActionResult {methodName}()\n";
            }
            else if (action == "GetMultiple" || action == "DeleteMultiple")
            {
                actionCode += $"    public IActionResult {methodName}([FromQuery]{parameterType} data)\n";
            }
            else
            {
                actionCode += $"    public IActionResult {methodName}({parameterType} data)\n";
            }

            actionCode += $"    {{\n";
            actionCode += $"        try\n";
            actionCode += $"        {{\n";

            // Modify the method call based on the action
            if (action == "Insert" || action == "Update" || action == "Delete" || action == "InsertMultiple" || action == "UpdateMultiple" || action == "DeleteMultiple")
            {
                actionCode += $"            int result = _{table.Name.ToLower()}Service.{methodName}(data);\n";
                actionCode += $"            if (result > 0)\n";
                actionCode += $"            {{\n";
                actionCode += $"                return Ok(\"Success. Id(s): \" + result);\n";
                actionCode += $"            }}\n";
                actionCode += $"            else\n";
                actionCode += $"            {{\n";
                actionCode += $"                return BadRequest(\"Failed to {action.ToLower()} data.\");\n";
                actionCode += $"            }}\n";
            }
            else
            {
                if (action == "GetAll")
                {
                    actionCode += $"            return Ok(_{table.Name.ToLower()}Service.{methodName}());\n";
                }
                else
                {
                    actionCode += $"            return Ok(_{table.Name.ToLower()}Service.{methodName}(data));\n";
                }

            }

            actionCode += $"        }}\n";
            actionCode += $"        catch (Exception e)\n";
            actionCode += $"        {{\n";
            actionCode += $"            _logger.LogError(e, e.Message);\n";
            actionCode += $"            return StatusCode(500, \"Unknown exception occurred: \" + e.Message);\n";
            actionCode += $"        }}\n";
            actionCode += $"    }}\n\n";

            return actionCode;
        }

        private IEnumerable<string> GetCrudActions()
        {
            return new List<string> { "GetAll", "GetById", "GetMultiple", "Insert", "InsertMultiple", "Update", "UpdateMultiple", "Delete", "DeleteMultiple" };
        }

        private string GetActionMethodName(string action)
        {
            return action switch
            {
                "GetAll" => "GetAll",
                "GetById" => "GetById",
                "GetMultiple" => "GetMultiple",
                "Insert" => "Insert",
                "InsertMultiple" => "InsertMultiple",
                "Update" => "Update",
                "UpdateMultiple" => "UpdateMultiple",
                "Delete" => "Delete",
                "DeleteMultiple" => "DeleteMultiple",
                _ => throw new ArgumentException($"Unsupported action: {action}"),
            };
        }

        private string GetActionParameterType(string action, Table table)
        {
            return action switch
            {
                "GetAll" => $"List<{table.EntityClass}>",
                "GetById" => $"int",
                "GetMultiple" => $"List<int>",
                "Insert" => $"{table.EntityClass}",
                "InsertMultiple" => $"List<{table.EntityClass}>",
                "Update" => $"{table.EntityClass}",
                "UpdateMultiple" => $"List<{table.EntityClass}>",
                "Delete" => $"int",
                "DeleteMultiple" => $"List<int>",
                _ => throw new ArgumentException($"Unsupported action: {action}"),
            };
        }

        public string GenerateInterfaceClass(Table table)
        {
            var interfaceCode = new StringBuilder();

            // Interface definition
            interfaceCode.AppendLine($"export interface {table.Name} {{");

            // Properties
            foreach (var column in table.Columns)
            {
                interfaceCode.AppendLine($"    {column.Name.ToLower()}: {GetMappedType(column.DataType)};");
            }

            // End of Interface
            interfaceCode.AppendLine("}");
            return interfaceCode.ToString();
        }

        private string GetMappedType(string dataType)
        {
            switch (dataType.ToLower())
            {
                case "int":
                case "int identity":
                case "bigint":
                case "smallint":
                case "tinyint":
                case "decimal":
                case "numeric":
                case "money":
                case "smallmoney":
                case "float":
                case "real":
                    return "number";
                case "nvarchar":
                case "varchar":
                case "char":
                case "nchar":
                case "text":
                case "ntext":
                case "xml":
                case "json":
                case "varchar(max)":
                case "nvarchar(max)":
                case "uniqueidentifier":
                case "time":
                case "datetimeoffset":
                    return "string";
                case "date":
                case "datetime":
                case "datetime2":
                case "smalldatetime":
                    return "Date";
                case "bit":
                    return "boolean";
                case "binary":
                case "varbinary":
                case "rowversion":
                case "timestamp":
                case "image":
                case "varbinary(max)":
                case "filestream":
                    return "Uint8Array"; // Represents byte[]
                case "geography":
                case "geometry":
                case "hierarchyid":
                case "sql_variant":
                case "table":
                    return "any";
                case "sql_variant_array":
                    return "any[]"; 
                default:
                    return "any";
            }
        }

        private string GenerateAngularTestServiceCode(Table table)
        {
            // Define the TypeScript code for the Angular service test
            var angularServiceCode = @$"import {{ TestBed }} from '@angular/core/testing';
import {{ {table.Name}Service }} from './{table.Name.ToLower()}.service';

describe('{table.Name}Service', () => {{
    let service: {table.Name}Service;

    beforeEach(() => {{
        TestBed.configureTestingModule({{}});
        service = TestBed.inject({table.Name}Service);
    }});

    it('should be created', () => {{
        expect(service).toBeTruthy();
    }});
}});";

            return angularServiceCode;
        }


        private string GenerateAngularServiceCode(Table table)
        {
            // Define the TypeScript code for the Angular service
            var angularServiceCode = @$"import {{ Injectable }} from '@angular/core';
import {{ HttpClient }} from '@angular/common/http';
import {{ Observable, throwError }} from 'rxjs';
import {{ catchError }} from 'rxjs/operators';
import {{ {table.Name} }} from '../{table.Name.ToLower()}';

@Injectable({{providedIn: 'root'
}})
export class {table.Name}Service {{
  private baseUrl = 'YourApiUrl/{table.Name}';

  constructor(private http: HttpClient) {{ }}

  getAll(): Observable<{table.Name}[]> {{
    return this.http.get<{table.Name}[]>(`${{this.baseUrl}}/GetAll`).pipe(
      catchError(this.handleError)
    );
  }}

  getById(id: number): Observable<{table.Name}> {{
    return this.http.get<{table.Name}>(`${{this.baseUrl}}/GetById?data=${{id}}`).pipe(
      catchError(this.handleError)
    );
  }}

  getMultiple(ids: number[]): Observable<{table.Name}[]> {{
    const queryParams = ids.map((id, index) => `data[${{index}}]=${{id}}`).join('&');
    return this.http.get<{table.Name}[]>(`${{this.baseUrl}}/GetMultiple?${{queryParams}}`).pipe(
      catchError(this.handleError)
    );
  }}

  insert({table.Name.ToLower()}: {table.Name}): Observable<{table.Name}> {{
    return this.http.post<{table.Name}>(`${{this.baseUrl}}/Insert`, {table.Name.ToLower()}).pipe(
      catchError(this.handleError)
    );
  }}

  insertMultiple({table.Name.ToLower()}s: {table.Name}[]): Observable<{table.Name}[]> {{
    return this.http.post<{table.Name}[]>(`${{this.baseUrl}}/InsertMultiple`, {table.Name.ToLower()}s).pipe(
      catchError(this.handleError)
    );
  }}

  update(id: number, {table.Name.ToLower()} : {table.Name}):Observable<{table.Name}> {{
    return this.http.put<{table.Name}>(`${{this.baseUrl}}/Update?data=${{id}}`, {table.Name.ToLower()}).pipe(
      catchError(this.handleError)
    );
  }}

  updateMultiple({table.Name.ToLower()}s: {table.Name}[]): Observable<{table.Name}[]> {{
    return this.http.put<{table.Name}[]>(`${{this.baseUrl}}/UpdateMultiple`, {table.Name.ToLower()}s).pipe(
      catchError(this.handleError)
    );
  }}

  delete(id: number):Observable<{table.Name}> {{
    return this.http.delete<{table.Name}>(`${{this.baseUrl}}/Delete?data=${{id}}`).pipe(
      catchError(this.handleError)
    );
  }}

  deleteMultiple(ids: {table.Name}[]): Observable<{table.Name}[]> {{
    const queryParams = ids.map((id, index) => `data[${{index}}]=${{id}}`).join('&');
    return this.http.delete<{table.Name}[]>(`${{this.baseUrl}}/DeleteMultiple?${{queryParams}}`).pipe(
      catchError(this.handleError)
    );
  }}

  private handleError(error: any): Observable<never> {{
    console.error('An error occurred:', error);
    return throwError('Something went wrong'); // Return an Observable of type never
  }}
}}";

            return angularServiceCode;
        }
        private string GenerateAngularMaterialTable(Table table)
        {
            List<ColumnModel> columns = table.Columns;

            // Generate the HTML snippet
            StringBuilder htmlBuilder = new StringBuilder();

            htmlBuilder.Append("<div class=\"mat-elevation-z8\">\n");
            htmlBuilder.Append("    <table mat-table [dataSource]=\"dataSource\">\n");

            foreach (ColumnModel column in columns)
            {
                htmlBuilder.Append($"        <ng-container matColumnDef=\"{column.Name.ToLower()}\">\n");
                htmlBuilder.Append($"            <th mat-header-cell *matHeaderCellDef>{column.Name}</th>\n");
                htmlBuilder.Append($"            <td mat-cell *matCellDef=\"let element\">{{{{element.{column.Name.ToLower()}}}}}</td>\n");
                htmlBuilder.Append("         </ng-container>\n");
            }

            htmlBuilder.Append("            <tr mat-header-row *matHeaderRowDef=\"displayedColumns\"></tr>\n");
            htmlBuilder.Append("            <tr mat-row *matRowDef=\"let row; columns: displayedColumns;\"></tr>\n");
            htmlBuilder.Append("    </table>\n");

            htmlBuilder.Append("    <mat-paginator [pageSizeOptions]=\"[5, 10, 20]\" showFirstLastButtons aria-label=\"Select page of elements\"></mat-paginator>\n");
            htmlBuilder.Append("</div>");

            return htmlBuilder.ToString();
        }

        private string GenerateAngularMaterialTableComponent(Table table)
        {
            // Generate the TypeScript file content
            StringBuilder tsBuilder = new StringBuilder();

            // Import statements
            tsBuilder.AppendLine("import {AfterViewInit, Component, ViewChild} from '@angular/core';");
            tsBuilder.AppendLine("import {MatPaginator} from '@angular/material/paginator';");
            tsBuilder.AppendLine("import {MatTableDataSource} from '@angular/material/table';");
            tsBuilder.AppendLine($"import {{ {table.Name}Service }} from '../{table.Name.ToLower()}-service/{table.Name.ToLower()}.service';");
            tsBuilder.AppendLine($"import {{ {table.Name} }} from '../{table.Name.ToLower()}';");

            tsBuilder.AppendLine();

            // Component definition
            tsBuilder.AppendLine("@Component({");
            tsBuilder.AppendLine($"  selector: '{table.Name}MatTable',");
            tsBuilder.AppendLine($"  templateUrl: '{table.Name}MatTable.html',");
            tsBuilder.AppendLine($"  styleUrls: ['{table.Name}MatTable.css'],");
            tsBuilder.AppendLine("})");

            // Class definition
            tsBuilder.AppendLine($"export class {table.Name}MatTable implements AfterViewInit {{");
            tsBuilder.AppendLine($"  displayedColumns: string[] = [");
            foreach (ColumnModel column in table.Columns)
            {
                tsBuilder.AppendLine($"'{column.Name.ToLower()}',");
            }
            tsBuilder.AppendLine("]");
            tsBuilder.AppendLine($"  dataSource: MatTableDataSource<{table.Name}>;");
                                                                                               // Inject service
            tsBuilder.AppendLine($"  constructor(private {table.Name.ToLower()}Service: {table.Name}Service) {{ ");
            tsBuilder.AppendLine($"     this.dataSource = new MatTableDataSource<{table.Name}>()");
            tsBuilder.AppendLine("  }");
            tsBuilder.AppendLine();
            tsBuilder.AppendLine("  @ViewChild(MatPaginator) paginator!: MatPaginator;");
            tsBuilder.AppendLine();
            tsBuilder.AppendLine("  ngAfterViewInit() {");
            tsBuilder.AppendLine("    this.fetchData();");
            tsBuilder.AppendLine("    this.dataSource.paginator = this.paginator;");
            tsBuilder.AppendLine("  }");
            // Fetch data method
            tsBuilder.AppendLine("  fetchData() {");
            tsBuilder.AppendLine($"    this.{table.Name.ToLower()}Service.getAll().subscribe((data) => {{this.dataSource.data = data}});");
            tsBuilder.AppendLine("  }");
            tsBuilder.AppendLine("}");

            return tsBuilder.ToString();
        }

        private string GenerateCSSFileForComponent(Table table, string method)
        {
            return $"/*Here you can add styling to {table.Name}-{method.ToLower()}*/";
        }

        private string GenerateTypeScriptSpecForComponent(Table table, string method)
        {
            StringBuilder tsSpec = new StringBuilder();

            tsSpec.AppendLine("import { ComponentFixture, TestBed } from '@angular/core/testing';");
            tsSpec.AppendLine();
            tsSpec.AppendLine($"import {{ {table.Name}{method}Component }} from './{table.Name.ToLower()}-{method.ToLower()}.component';");
            tsSpec.AppendLine();
            tsSpec.AppendLine($"describe('{table.Name}{method}Component', () => {{");
            tsSpec.AppendLine($"  let component: {table.Name}{method}Component;");
            tsSpec.AppendLine($"  let fixture: ComponentFixture<{table.Name}{method}Component>;");
            tsSpec.AppendLine();
            tsSpec.AppendLine("  beforeEach(async () => {");
            tsSpec.AppendLine("    await TestBed.configureTestingModule({");
            tsSpec.AppendLine($"      declarations: [ {table.Name}{method}Component ]");
            tsSpec.AppendLine("    })");
            tsSpec.AppendLine("    .compileComponents();");
            tsSpec.AppendLine();
            tsSpec.AppendLine($"    fixture = TestBed.createComponent({table.Name}{method}Component);");
            tsSpec.AppendLine($"    component = fixture.componentInstance;");
            tsSpec.AppendLine("    fixture.detectChanges();");
            tsSpec.AppendLine("  });");
            tsSpec.AppendLine();
            tsSpec.AppendLine("  it('should create', () => {");
            tsSpec.AppendLine("    expect(component).toBeTruthy();");
            tsSpec.AppendLine("  });");
            tsSpec.AppendLine("});");

            return tsSpec.ToString();
        }

        private string GenerateInsertComponent(Table table)
        {
            return $@"
import {{ Component }} from '@angular/core';
import {{ {table.Name}Service }} from 'src/app/services/{table.Name.ToLower()}/{table.Name.ToLower()}.service';
import {{ {table.Name} }} from 'src/app/{table.Name.ToLower()}';

@Component({{
  selector: 'app-{table.Name.ToLower()}-insert',
  templateUrl: './{table.Name.ToLower()}-insert.component.html',
  styleUrls: ['./{table.Name.ToLower()}-insert.component.css']
}})
export class {table.Name}InsertComponent {{
  {table.Name.ToLower()}: {table.Name} = new {table.Name}();

  constructor(private {table.Name.ToLower()}Service: {table.Name}Service) {{}}

  onSubmit(): void {{
    this.{table.Name.ToLower()}Service.insert(this.{table.Name.ToLower()}).subscribe(
      (response) => {{
        console.log('{table.Name} added successfully:', response);
        alert(`{table.Name} has been added successfully`);
        this.resetForm(); // Optional: Reset form after successful submission
      }},
      (error) => {{
        console.error('Failed to add {table.Name.ToLower()}:', error);
        alert('Failed to add {table.Name.ToLower()}: ' + error.message);
      }}
    );
  }}

  // Optional: Function to reset the form after successful submission
  private resetForm(): void {{
    this.{table.Name.ToLower()} = new {table.Name}();
  }}
}}
";
        }
        private string GenerateUpdateComponent(Table table)
        {
            return $@"
import {{ Component, OnInit }} from '@angular/core';
import {{ ActivatedRoute }} from '@angular/router';
import {{ {table.Name}Service }} from 'src/app/services/{table.Name.ToLower()}/{table.Name.ToLower()}.service';

@Component({{
  selector: 'app-{table.Name.ToLower()}-update',
  templateUrl: './{table.Name.ToLower()}-update.component.html',
  styleUrls: ['./{table.Name.ToLower()}-update.component.css']
}})
export class {table.Name}UpdateComponent implements OnInit {{
  {table.Name.ToLower()}Id!: number;
  {table.Name.ToLower()}: any = {{}}; // Object to hold {table.Name.ToLower()} data

  constructor(private route: ActivatedRoute, private {table.Name.ToLower()}Service: {table.Name}Service) {{ }}

  ngOnInit(): void {{
    // Retrieve {table.Name.ToLower()} ID from route parameters
    const {table.Name.ToLower()}IdString = this.route.snapshot.paramMap.get('id');
  
    if ({table.Name.ToLower()}IdString !== null) {{
      this.{table.Name.ToLower()}Id = +{table.Name.ToLower()}IdString; // Convert string to number
      // Fetch {table.Name.ToLower()} details by ID
      this.{table.Name.ToLower()}Service.getById(this.{table.Name.ToLower()}Id).subscribe(
        (data) => {{
          this.{table.Name.ToLower()} = data;
        }},
        (error) => {{
          console.error('Failed to fetch {table.Name.ToLower()} details:', error);
        }}
      );
    }} else {{
      console.error('{table.Name} ID not found in route parameters.');
    }}
  }}

  onSubmit(): void {{
    this.{table.Name.ToLower()}Service.update(this.{table.Name.ToLower()}Id, this.{table.Name.ToLower()}).subscribe(
      (response) => {{
        // Handle successful update
        alert(`{table.Name} updated successfully`);
      }},
      (error) => {{
        // Handle error
        alert('Failed to update {table.Name.ToLower()}: ' + error);
      }}
    );
  }}
}}
";
        }

        private string GenerateDeleteComponent(Table table)
        {
            return $@"
import {{ Component, OnInit }} from '@angular/core';
import {{ ActivatedRoute, Router }} from '@angular/router';
import {{ {table.Name}Service }} from 'src/app/services/{table.Name.ToLower()}/{table.Name.ToLower()}.service';

@Component({{
  selector: 'app-{table.Name.ToLower()}-delete',
  templateUrl: './{table.Name.ToLower()}-delete.component.html',
  styleUrls: ['./{table.Name.ToLower()}-delete.component.css']
}})
export class {table.Name}DeleteComponent implements OnInit {{
  {table.Name.ToLower()}Id!: number;
  {table.Name.ToLower()}: any = {{}}; // Object to hold {table.Name.ToLower()} data

  constructor(private route: ActivatedRoute, private router: Router, private {table.Name.ToLower()}Service: {table.Name}Service) {{ }}

  ngOnInit(): void {{
    // Retrieve {table.Name.ToLower()} ID from route parameters
    const {table.Name.ToLower()}IdString = this.route.snapshot.paramMap.get('id');
  
    if ({table.Name.ToLower()}IdString !== null) {{
      this.{table.Name.ToLower()}Id = +{table.Name.ToLower()}IdString; // Convert string to number
      // Fetch {table.Name.ToLower()} details by ID
      this.{table.Name.ToLower()}Service.getById(this.{table.Name.ToLower()}Id).subscribe(
        (data) => {{
          this.{table.Name.ToLower()} = data;
        }},
        (error) => {{
          console.error('Failed to fetch {table.Name.ToLower()} details:', error);
        }}
      );
    }} else {{
      console.error('{table.Name} ID not found in route parameters.');
    }}
  }}

  onDelete(): void {{
    this.{table.Name.ToLower()}Service.delete(this.{table.Name.ToLower()}Id).subscribe(
      () => {{
        // Handle successful deletion
        alert('{table.Name} deleted successfully');
        // Navigate back to {table.Name.ToLower()} list or any other appropriate route
        this.router.navigate(['/{{table.Name.ToLower()}}s']);
      }},
      (error) => {{
        // Handle error
        alert('Failed to delete {table.Name.ToLower()}: ' + error);
      }}
    );
  }}
}}
";
        }

        private string GenerateGetByIdComponent(Table table)
        {
            return $@"
import {{ Component, OnInit }} from '@angular/core';
import {{ ActivatedRoute }} from '@angular/router';
import {{ {table.Name}Service }} from '../../services/{table.Name.ToLower()}/{table.Name.ToLower()}.service';

@Component({{
  selector: 'app-{table.Name.ToLower()}-get',
  templateUrl: './{table.Name.ToLower()}-get.component.html',
  styleUrls: ['./{table.Name.ToLower()}-get.component.css']
}})
export class {table.Name}GetComponent implements OnInit {{
  id: any;
  {table.Name.ToLower()}: any;
  errorMessage: string | undefined;

  constructor(private route: ActivatedRoute, private {table.Name.ToLower()}Service: {table.Name}Service) {{}}

  ngOnInit(): void {{
    this.id = this.route.snapshot.paramMap.get('id');
    if (this.id) {{
      this.get{table.Name}ById(this.id);
    }} else {{
      this.errorMessage = 'No ID provided in route.';
    }}
  }}

  get{table.Name}ById(id: any): void {{
    this.{table.Name.ToLower()}Service.getById(id).subscribe(
      ({table.Name.ToLower()}: any) => {{
        this.{table.Name.ToLower()} = {table.Name.ToLower()};
      }},
      (error: any) => {{
        this.errorMessage = 'Failed to fetch {table.Name.ToLower()}. Please try again later.';
      }}
    );
  }}
}}
";
        }

        public string GenerateTypescriptForComponent(Table table, string method)
        {
            StringBuilder ts = new StringBuilder();

            switch (method.ToLower())
            {
                case "insert":
                    ts.Append(GenerateInsertComponent(table));
                    break;
                case "update":
                    ts.Append(GenerateUpdateComponent(table));
                    break;
                case "delete":
                    ts.Append(GenerateDeleteComponent(table));
                    break;
                case "getbyid":
                    ts.Append(GenerateGetByIdComponent(table));
                    break;
                default:
                    throw new ArgumentException("Invalid method type");
            }

            return ts.ToString();
        }

        private string GetInputType(string columnType)
        {
            switch (columnType.ToLower())
            {
                case "string":
                    return "text";
                case "int":
                case "integer":
                case "smallint":
                case "bigint":
                    return "number";
                case "decimal":
                case "float":
                case "double":
                case "real":
                    return "number";
                case "date":
                    return "date";
                case "datetime":
                case "timestamp":
                    return "datetime-local";
                case "time":
                    return "time";
                case "boolean":
                case "bool":
                    return "checkbox";
                case "email":
                    return "email";
                case "password":
                    return "password";
                case "url":
                    return "url";
                case "tel":
                    return "tel";
                case "textarea":
                    return "textarea";
                default:
                    return "text";
            }
        }



        private string GenerateInsertHtml(Table table)
        {
            StringBuilder html = new StringBuilder();
            html.Append($@"
<div class=""insert-container"">
  <h2>Add New {table.Name}</h2>
  <form (submit)=""onSubmit()"">");

            foreach (var column in table.Columns)
            {
                html.Append($@"
    <div>
      <label for=""{column.Name}"">{column.Name}:</label>
      <input type=""{GetInputType(column.DataType)}"" id=""{column.Name}"" name=""{column.Name}"" [(ngModel)]=""{table.Name.ToLower()}.{column.Name.ToLower()}"" required>
    </div>");
            }

            html.Append($@"
    <button type=""submit"">Submit</button>
  </form>
</div>");

            return html.ToString();
        }

        private string GenerateUpdateHtml(Table table)
        {
            StringBuilder html = new StringBuilder();
            html.Append($@"
<div class=""update-container"">
  <h2>Update {table.Name}</h2>
  <form (submit)=""onSubmit()"">");

            foreach (var column in table.Columns)
            {
                html.Append($@"
    <div>
      <label for=""{column.Name}"">{column.Name}:</label>
      <input type=""{GetInputType(column.DataType)}"" id=""{column.Name}"" name=""{column.Name}"" [(ngModel)]=""{table.Name.ToLower()}.{column.Name.ToLower()}"" required>
    </div>");
            }

            html.Append($@"
    <button type=""submit"">Update</button>
  </form>
</div>");

            return html.ToString();
        }

        private string GenerateDeleteHtml(Table table)
        {
            return $@"
<div class=""delete-container"">
  <h2>Delete {table.Name}</h2>
  <p>Are you sure you want to delete this {table.Name.ToLower()}?</p>
  <p><strong>{{{{ {table.Name.ToLower()}.nom }}}} {{{{ {table.Name.ToLower()}.prenom }}}}</strong></p>
  <button (click)=""onDelete()"">Delete</button>
</div>";
        }

        private string GenerateGetByIdHtml(Table table)
        {
            StringBuilder html = new StringBuilder();
            html.Append($@"
<div class=""get-container"" *ngIf=""{table.Name.ToLower()}; else loading"">
  <h2>{table.Name} Details</h2>");

            foreach (var column in table.Columns)
            {
                html.Append($@"
    <p><strong>{column.Name}:</strong> {{{{ {table.Name.ToLower()}.{column.Name.ToLower()} }}}}</p>");
            }

            html.Append($@"
</div>

<ng-template #loading>
  <p>Loading...</p>
</ng-template>

<p *ngIf=""errorMessage"">{{{{ errorMessage }}}}</p>");

            return html.ToString();
        }





        public IActionResult Index()
        {
            return View();
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}

