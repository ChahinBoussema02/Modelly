🧩 Modelly

MyModelly is a full web application built to automate the generation of C# models and CRUD operations from database schemas.

This repository contains only the backend part of the project, built with ASP.NET Core and Entity Framework Core. It handles schema analysis, model generation, and exposes secure APIs for integration.

🛠️ Tech Stack

Language: C#
Framework: ASP.NET Core
ORM: Entity Framework Core
Auth: JWT (JSON Web Token)
Database: SQL Server
🚀 Getting Started

Prerequisites
.NET SDK 7.0+
SQL Server
Steps
git clone https://github.com/ChahinBoussema02/MyModelly.git
cd MyModelly
dotnet restore
# Edit appsettings.json with your DB connection string
dotnet ef database update
dotnet run
