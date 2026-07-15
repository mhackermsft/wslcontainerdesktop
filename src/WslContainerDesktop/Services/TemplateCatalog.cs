// WSL Container Desktop - a WinUI 3 manager for WSL containers.
// Copyright (C) 2026 Michael Hacker
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Provides the curated set of one-click stack templates shown in the gallery.</summary>
public interface ITemplateCatalog
{
    IReadOnlyList<StackTemplate> Templates { get; }
}

/// <summary>
/// A static, bundled catalog of popular images and multi-service stacks. All templates use public
/// images (Docker Hub or Microsoft Container Registry) so they run without registry credentials.
/// Single-container templates prefill the Run dialog; compose templates import as a project. Data
/// services use a named volume so restarts don't lose state; developer sandboxes stay alive with a
/// keep-alive command and mount a persistent <c>/workspace</c> volume.
/// </summary>
public sealed class TemplateCatalog : ITemplateCatalog
{
    public IReadOnlyList<StackTemplate> Templates { get; } = Build();

    private static IReadOnlyList<StackTemplate> Build()
    {
        return new List<StackTemplate>
        {
            // ---- Databases ----
            new()
            {
                Id = "postgres",
                Name = "PostgreSQL",
                Category = "Databases",
                Description = "Postgres 16 relational database on port 5432.",
                Glyph = "\uE7B8",
                Note = "Default credentials: user \"postgres\", password \"postgres\".",
                RunOptions = new RunContainerOptions
                {
                    Image = "postgres:16",
                    Name = "postgres",
                    PortMappings = { "5432:5432" },
                    EnvironmentVariables = { "POSTGRES_PASSWORD=postgres" },
                    Volumes = { "postgres-data:/var/lib/postgresql/data" },
                },
            },
            new()
            {
                Id = "mysql",
                Name = "MySQL",
                Category = "Databases",
                Description = "MySQL 8 relational database on port 3306.",
                Glyph = "\uE7B8",
                Note = "Default credentials: user \"root\", password \"mysql\".",
                RunOptions = new RunContainerOptions
                {
                    Image = "mysql:8",
                    Name = "mysql",
                    PortMappings = { "3306:3306" },
                    EnvironmentVariables = { "MYSQL_ROOT_PASSWORD=mysql" },
                    Volumes = { "mysql-data:/var/lib/mysql" },
                },
            },
            new()
            {
                Id = "mongo",
                Name = "MongoDB",
                Category = "Databases",
                Description = "MongoDB 7 document database on port 27017.",
                Glyph = "\uE7B8",
                RunOptions = new RunContainerOptions
                {
                    Image = "mongo:7",
                    Name = "mongo",
                    PortMappings = { "27017:27017" },
                    Volumes = { "mongo-data:/data/db" },
                },
            },
            new()
            {
                Id = "redis",
                Name = "Redis",
                Category = "Databases",
                Description = "Redis 7 in-memory key-value store on port 6379.",
                Glyph = "\uE7B8",
                RunOptions = new RunContainerOptions
                {
                    Image = "redis:7",
                    Name = "redis",
                    PortMappings = { "6379:6379" },
                    Volumes = { "redis-data:/data" },
                },
            },

            // ---- Web & tools ----
            new()
            {
                Id = "nginx",
                Name = "Nginx",
                Category = "Web & tools",
                Description = "Nginx web server, published on http://localhost:8080.",
                Glyph = "\uE774",
                RunOptions = new RunContainerOptions
                {
                    Image = "nginx:alpine",
                    Name = "nginx",
                    PortMappings = { "8080:80" },
                },
            },
            new()
            {
                Id = "adminer",
                Name = "Adminer",
                Category = "Web & tools",
                Description = "Lightweight database admin UI on http://localhost:8081.",
                Glyph = "\uE774",
                RunOptions = new RunContainerOptions
                {
                    Image = "adminer:latest",
                    Name = "adminer",
                    PortMappings = { "8081:8080" },
                },
            },
            new()
            {
                Id = "minio",
                Name = "MinIO",
                Category = "Web & tools",
                Description = "S3-compatible object storage. API :9000, console http://localhost:9001.",
                Glyph = "\uE7B8",
                Note = "Default credentials: user \"minioadmin\", password \"minioadmin\".",
                RunOptions = new RunContainerOptions
                {
                    Image = "minio/minio:latest",
                    Name = "minio",
                    PortMappings = { "9000:9000", "9001:9001" },
                    EnvironmentVariables =
                    {
                        "MINIO_ROOT_USER=minioadmin",
                        "MINIO_ROOT_PASSWORD=minioadmin",
                    },
                    Volumes = { "minio-data:/data" },
                    Command = "server /data --console-address :9001",
                },
            },
            new()
            {
                Id = "rabbitmq",
                Name = "RabbitMQ",
                Category = "Web & tools",
                Description = "Message broker with management UI on http://localhost:15672.",
                Glyph = "\uE7B8",
                Note = "Default credentials: user \"guest\", password \"guest\".",
                RunOptions = new RunContainerOptions
                {
                    Image = "rabbitmq:3-management",
                    Name = "rabbitmq",
                    PortMappings = { "5672:5672", "15672:15672" },
                    Volumes = { "rabbitmq-data:/var/lib/rabbitmq" },
                },
            },

            // ---- Developer sandboxes ----
            // Language toolchains kept alive with `sleep infinity` so you can open the container's
            // Terminal action and work at a shell. Source lives in a persistent /workspace volume.
            new()
            {
                Id = "dev-python",
                Name = "Python",
                Category = "Developer",
                Description = "Python 3.14 sandbox. Open the Terminal to run python/pip.",
                Glyph = "\uE943",
                Note = "Keep-alive sandbox — open the container's Terminal for a shell. "
                    + "Files persist in the \"python-workspace\" volume at /workspace. "
                    + "Published port 8000 for a dev server (e.g. python -m http.server 8000).",
                RunOptions = new RunContainerOptions
                {
                    Image = "python:3.14",
                    Name = "python-dev",
                    PortMappings = { "8000:8000" },
                    Volumes = { "python-workspace:/workspace" },
                    WorkingDir = "/workspace",
                    Command = "sleep infinity",
                },
            },
            new()
            {
                Id = "dev-node",
                Name = "Node.js",
                Category = "Developer",
                Description = "Node.js 24 LTS sandbox. Open the Terminal to run node/npm.",
                Glyph = "\uE943",
                Note = "Keep-alive sandbox — open the container's Terminal for a shell. "
                    + "Files persist in the \"node-workspace\" volume at /workspace. "
                    + "Published port 3000 for a dev server.",
                RunOptions = new RunContainerOptions
                {
                    Image = "node:24",
                    Name = "node-dev",
                    PortMappings = { "3000:3000" },
                    Volumes = { "node-workspace:/workspace" },
                    WorkingDir = "/workspace",
                    Command = "sleep infinity",
                },
            },
            new()
            {
                Id = "dev-dotnet",
                Name = ".NET SDK",
                Category = "Developer",
                Description = ".NET 10 SDK sandbox. Open the Terminal to run dotnet.",
                Glyph = "\uE943",
                Note = "Keep-alive sandbox — open the container's Terminal for a shell "
                    + "(try \"dotnet new web\"). Files persist in the \"dotnet-workspace\" volume "
                    + "at /workspace. Host port 5080 maps to the ASP.NET Core default port 8080.",
                RunOptions = new RunContainerOptions
                {
                    Image = "mcr.microsoft.com/dotnet/sdk:10.0",
                    Name = "dotnet-dev",
                    PortMappings = { "5080:8080" },
                    Volumes = { "dotnet-workspace:/workspace" },
                    WorkingDir = "/workspace",
                    Command = "sleep infinity",
                },
            },
            new()
            {
                Id = "dev-java",
                Name = "Java",
                Category = "Developer",
                Description = "Temurin 25 LTS JDK sandbox. Open the Terminal to run java/javac.",
                Glyph = "\uE943",
                Note = "Keep-alive sandbox — open the container's Terminal for a shell. "
                    + "Files persist in the \"java-workspace\" volume at /workspace. "
                    + "Published port 8080 for a dev server (e.g. Spring Boot).",
                RunOptions = new RunContainerOptions
                {
                    Image = "eclipse-temurin:25-jdk",
                    Name = "java-dev",
                    PortMappings = { "8080:8080" },
                    Volumes = { "java-workspace:/workspace" },
                    WorkingDir = "/workspace",
                    Command = "sleep infinity",
                },
            },
            new()
            {
                Id = "dev-go",
                Name = "Go",
                Category = "Developer",
                Description = "Go 1.26 sandbox. Open the Terminal to run go build/run.",
                Glyph = "\uE943",
                Note = "Keep-alive sandbox — open the container's Terminal for a shell. "
                    + "Files persist in the \"go-workspace\" volume at /workspace. "
                    + "Host port 8090 maps to container port 8080 for a dev server.",
                RunOptions = new RunContainerOptions
                {
                    Image = "golang:1.26",
                    Name = "go-dev",
                    PortMappings = { "8090:8080" },
                    Volumes = { "go-workspace:/workspace" },
                    WorkingDir = "/workspace",
                    Command = "sleep infinity",
                },
            },
            new()
            {
                Id = "dev-rust",
                Name = "Rust",
                Category = "Developer",
                Description = "Rust (latest stable) sandbox. Open the Terminal to run cargo/rustc.",
                Glyph = "\uE943",
                Note = "Keep-alive sandbox — open the container's Terminal for a shell "
                    + "(try \"cargo new app\"). Files persist in the \"rust-workspace\" volume "
                    + "at /workspace. Host port 8091 maps to container port 8080 for a dev server.",
                RunOptions = new RunContainerOptions
                {
                    Image = "rust:1",
                    Name = "rust-dev",
                    PortMappings = { "8091:8080" },
                    Volumes = { "rust-workspace:/workspace" },
                    WorkingDir = "/workspace",
                    Command = "sleep infinity",
                },
            },

            // ---- Stacks (multi-service compose) ----
            new()
            {
                Id = "wordpress-mysql",
                Name = "WordPress + MySQL",
                Category = "Stacks",
                Description = "WordPress on http://localhost:8082 backed by a MySQL database.",
                Glyph = "\uE909",
                Kind = StackTemplateKind.Compose,
                ComposeProjectName = "wordpress",
                Note = "WordPress: http://localhost:8082. Complete setup in the browser on first run.",
                ComposeYaml = WordPressMySqlYaml,
            },
            new()
            {
                Id = "postgres-pgadmin",
                Name = "PostgreSQL + pgAdmin",
                Category = "Stacks",
                Description = "Postgres 16 with the pgAdmin 4 web console on http://localhost:8083.",
                Glyph = "\uE909",
                Kind = StackTemplateKind.Compose,
                ComposeProjectName = "pgadmin",
                Note = "pgAdmin: http://localhost:8083 — login admin@example.com / admin. "
                    + "Add a server for host \"db\", user/password postgres.",
                ComposeYaml = PostgresPgAdminYaml,
            },
        };
    }

    private const string WordPressMySqlYaml = """
        services:
          db:
            image: mysql:8
            environment:
              MYSQL_ROOT_PASSWORD: wordpress
              MYSQL_DATABASE: wordpress
              MYSQL_USER: wordpress
              MYSQL_PASSWORD: wordpress
            volumes:
              - wp-db:/var/lib/mysql
          wordpress:
            image: wordpress:latest
            depends_on:
              - db
            ports:
              - "8082:80"
            environment:
              WORDPRESS_DB_HOST: db
              WORDPRESS_DB_USER: wordpress
              WORDPRESS_DB_PASSWORD: wordpress
              WORDPRESS_DB_NAME: wordpress
            volumes:
              - wp-content:/var/www/html
        volumes:
          wp-db:
          wp-content:
        """;

    private const string PostgresPgAdminYaml = """
        services:
          db:
            image: postgres:16
            environment:
              POSTGRES_PASSWORD: postgres
            volumes:
              - pg-data:/var/lib/postgresql/data
          pgadmin:
            image: dpage/pgadmin4:latest
            depends_on:
              - db
            ports:
              - "8083:80"
            environment:
              PGADMIN_DEFAULT_EMAIL: admin@example.com
              PGADMIN_DEFAULT_PASSWORD: admin
            volumes:
              - pgadmin-data:/var/lib/pgadmin
        volumes:
          pg-data:
          pgadmin-data:
        """;
}
