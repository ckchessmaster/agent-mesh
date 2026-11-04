using AgentMesh.Api.Controllers;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Controllers;

/// <summary>
/// The project supporting-document library's HTTP contract: what a document is, where its bytes live,
/// and what a rejected request refuses to write.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why these tests write to the real filesystem.</strong> The behaviour under test is that a
/// document's bytes land at a specific path and are found again from that path. The alternative —
/// abstracting the filesystem behind an interface — would mean production code gains a seam only the
/// test uses, and the thing the test then asserts is the mock rather than the path. The existing
/// attachment tests in <see cref="ProjectJobsControllerTests"/> already write to
/// <c>~/.agentmesh/artifacts/jobs/…</c> for the same reason; these follow suit under
/// <c>~/.agentmesh/artifacts/projects/…</c> and clean up after themselves.
/// </para>
/// <para>
/// Each test uses a distinct project slug so parallel runs cannot collide on a directory or on the
/// in-memory database, and disposes its own context.
/// </para>
/// </remarks>
public class ProjectsControllerDocumentTests : IDisposable
{
    private readonly List<string> _projectIds = new();

    /// <summary>
    /// A project slug unique to this test, with its document directory removed at teardown.
    /// </summary>
    private string NewProjectId([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        // The caller name keeps slugs readable in failure output ("docs-uploadfile-abc…") instead of an
        // opaque guid per test. Uniqueness comes from the guid, because parallel tests in one class share
        // the filesystem and a repeated slug would have one test delete another's directory.
        var suffix = Guid.NewGuid().ToString("N")[..12];

        // The suffix leads so the 64-character truncation (Project.id is character varying(64)) can only
        // ever eat the readable name, never the part that makes the slug unique. A slug that collided
        // would have one test delete another's document directory mid-run.
        var slug = $"docs-{suffix}-{caller.ToLowerInvariant()}";
        slug = slug.Length > 64 ? slug[..64] : slug;

        _projectIds.Add(slug);
        return slug;
    }

    public void Dispose()
    {
        foreach (var id in _projectIds)
        {
            try
            {
                var dir = ProjectsController.GetDocumentDirectory(id);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

                // The parent is created by GetDocumentDirectory too; remove it when this test's project
                // was the only thing inside so repeated runs do not litter the artifacts tree.
                var parent = Path.GetDirectoryName(dir);
                if (parent is not null && Directory.Exists(parent) && Directory.GetFileSystemEntries(parent).Length == 0)
                {
                    Directory.Delete(parent);
                }
            }
            catch
            {
                // Teardown noise must not fail a passing test; the next run uses a fresh slug anyway.
            }
        }
    }

    private static Project NewProject(string id) => new()
    {
        Id = id,
        Name = "Document Library Project",
        WorkspaceType = WorkspaceType.Git
    };

    private static ProjectsController Controller(AgentMeshDbContext db, IStorageService? storage = null)
    {
        var controller = new ProjectsController(db, hubContext: null, storageService: storage);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static IFormFileCollection Files(params (string Name, string ContentType, byte[] Bytes)[] files)
    {
        var collection = new FormFileCollection();
        foreach (var (name, contentType, bytes) in files)
        {
            var stream = new MemoryStream(bytes);
            collection.Add(new FormFile(stream, 0, bytes.Length, "files", name)
            {
                Headers = new HeaderDictionary(),
                ContentType = contentType
            });
        }

        return collection;
    }

    private static byte[] Bytes(int length = 5) => new byte[length];

    // ── list ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetProjectDocuments_UnknownProject_404()
    {
        using var db = TestDbContextFactory.Create();
        var controller = Controller(db);

        var result = await controller.GetProjectDocuments("no-such-project", default);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetProjectDocuments_EmptyLibrary_ReturnsEmptyListNotError()
    {
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        await db.SaveChangesAsync();

        var result = await Controller(db).GetProjectDocuments(id, default);

        var list = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<List<ProjectDocumentDto>>().Subject;
        list.Should().BeEmpty();
    }

    [Fact]
    public async Task GetProjectDocuments_OmitsBodiesButKeepsEveryLocator()
    {
        // The list is what the dashboard renders and what the index is built from. A body in the list
        // would mean 50 pasted specs crossing the wire to draw 50 rows; a missing doc id or URL would
        // mean the row cannot be opened.
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        db.ProjectDocuments.Add(new ProjectDocument
        {
            ProjectId = id,
            Title = "API contract",
            Kind = ProjectDocumentKind.Text,
            Content = "POST /things creates a thing."
        });
        await db.SaveChangesAsync();

        var list = (await Controller(db).GetProjectDocuments(id, default))
            .Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<List<ProjectDocumentDto>>().Subject;

        var dto = list.Should().ContainSingle().Subject;
        dto.Content.Should().BeNull("the list endpoint must not ship bodies");
        dto.Title.Should().Be("API contract");
        dto.Id.Should().NotBeEmpty();
        dto.DownloadUrl.Should().Be($"/api/projects/{id}/documents/{dto.Id}/content");
        dto.ForceDownloadUrl.Should().Be(dto.DownloadUrl + "?download=true");
    }

    [Fact]
    public async Task GetProjectDocuments_OrdersByTitleThenId()
    {
        // The prompt index caps at MaxRenderedDocuments and keeps the head of this order. If the list
        // endpoint ordered differently, the dashboard would show a set of documents the agent was never
        // told about.
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        db.ProjectDocuments.AddRange(
            new ProjectDocument { ProjectId = id, Title = "Zeta spec", Kind = ProjectDocumentKind.Text, Content = "z" },
            new ProjectDocument { ProjectId = id, Title = "Alpha spec", Kind = ProjectDocumentKind.Text, Content = "a" },
            new ProjectDocument { ProjectId = id, Title = "Mid spec", Kind = ProjectDocumentKind.Text, Content = "m" });
        await db.SaveChangesAsync();

        var list = (await Controller(db).GetProjectDocuments(id, default))
            .Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<List<ProjectDocumentDto>>().Subject;

        list.Select(d => d.Title).Should().ContainInOrder("Alpha spec", "Mid spec", "Zeta spec");
    }

    [Fact]
    public async Task GetProjectDocuments_ScopedToItsProject()
    {
        var mine = NewProjectId();
        var theirs = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.AddRange(NewProject(mine), NewProject(theirs));
        db.ProjectDocuments.Add(new ProjectDocument { ProjectId = mine, Title = "Mine", Kind = ProjectDocumentKind.Text, Content = "1" });
        db.ProjectDocuments.Add(new ProjectDocument { ProjectId = theirs, Title = "Theirs", Kind = ProjectDocumentKind.Text, Content = "2" });
        await db.SaveChangesAsync();

        var list = (await Controller(db).GetProjectDocuments(mine, default))
            .Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<List<ProjectDocumentDto>>().Subject;

        list.Should().ContainSingle().Which.Title.Should().Be("Mine");
    }

    // ── create: text ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateProjectDocuments_Text_PersistsRowAndReturnsDtoWithContent()
    {
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        await db.SaveChangesAsync();

        var result = await Controller(db).CreateProjectDocuments(id, null, "Sprint 12 notes", "Ship the library feature.", default);

        var list = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<List<ProjectDocumentDto>>().Subject;

        var dto = list.Should().ContainSingle().Subject;
        dto.Title.Should().Be("Sprint 12 notes");
        dto.Kind.Should().Be(ProjectDocumentKind.Text);
        dto.FileName.Should().BeNull("a pasted document has no filename");
        dto.S3Key.Should().BeNull("a pasted document has no object to mirror");
        dto.SizeBytes.Should().Be(System.Text.Encoding.UTF8.GetByteCount("Ship the library feature."));

        var row = await db.ProjectDocuments.AsNoTracking().SingleAsync(d => d.Id == Guid.Parse(dto.Id));
        row.Content.Should().Be("Ship the library feature.");
        row.ProjectId.Should().Be(id);
    }

    [Fact]
    public async Task CreateProjectDocuments_TextWithoutTitle_DefaultsToFirstContentLine()
    {
        // "Untitled document" in an index tells an agent nothing about whether to fetch it, and the
        // first line of pasted prose is usually its own heading.
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        await db.SaveChangesAsync();

        var body = "  \n# Architecture Overview\nbody text";
        var result = await Controller(db).CreateProjectDocuments(id, null, null, body, default);

        var list = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<List<ProjectDocumentDto>>().Subject;
        list.Should().ContainSingle().Which.Title.Should().Be("# Architecture Overview");
    }

    [Fact]
    public async Task CreateProjectDocuments_NoFilesAndNoText_400()
    {
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        await db.SaveChangesAsync();

        var result = await Controller(db).CreateProjectDocuments(id, null, "title only", null, default);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await db.ProjectDocuments.CountAsync()).Should().Be(0,
            "a title with no body must not create a document");
    }

    [Fact]
    public async Task CreateProjectDocuments_WhitespaceContent_400()
    {
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        await db.SaveChangesAsync();

        var result = await Controller(db).CreateProjectDocuments(id, null, "t", "   \n  ", default);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CreateProjectDocuments_OversizedText_400AndWritesNothing()
    {
        // The cap exists because a text body is the one body the prompt can inline. The test asserts the
        // 400 AND the absence of a row: a limit that rejects the response but keeps the row is a limit
        // that silently truncates nothing and stores everything.
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        await db.SaveChangesAsync();

        var oversized = new string('x', ProjectDocument.MaxTextContentChars + 1);
        var result = await Controller(db).CreateProjectDocuments(id, null, "big", oversized, default);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await db.ProjectDocuments.CountAsync(d => d.ProjectId == id)).Should().Be(0);
    }

    [Fact]
    public async Task CreateProjectDocuments_UnknownProject_404()
    {
        using var db = TestDbContextFactory.Create();

        var result = await Controller(db).CreateProjectDocuments("missing", null, "t", "body", default);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    // ── create: files ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateProjectDocuments_File_WritesBytesToDiskAndRecordsS3Key()
    {
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        await db.SaveChangesAsync();

        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var result = await Controller(db).CreateProjectDocuments(id, Files(("spec-diagram.png", "image/png", png)), null, null, default);

        var dto = (result.Should().BeOfType<OkObjectResult>().Subject.Value as List<ProjectDocumentDto>)![0];
        dto.Kind.Should().Be(ProjectDocumentKind.File);
        dto.FileName.Should().Be("spec-diagram.png");
        dto.ContentType.Should().Be("image/png");
        dto.Title.Should().Be("spec-diagram.png", "an upload's title defaults to its filename");
        dto.S3Key.Should().Be($"projects/{id}/documents/{dto.Id}/spec-diagram.png");

        var path = ProjectsController.LocalDocumentPath(
            ProjectsController.GetDocumentDirectory(id), Guid.Parse(dto.Id), "spec-diagram.png");
        System.IO.File.Exists(path).Should().BeTrue("the local copy is what the read path tries first");
        (await System.IO.File.ReadAllBytesAsync(path)).Should().Equal(png);
    }

    [Fact]
    public async Task CreateProjectDocuments_FileAndText_InOneCall_CreatesBothKinds()
    {
        // The whole reason for one endpoint: the dashboard's add flow lets a user paste notes and attach
        // a screenshot in the same submit.
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        await db.SaveChangesAsync();

        var result = await Controller(db).CreateProjectDocuments(
            id, Files(("diagram.svg", "image/svg+xml", "<svg/>"u8.ToArray())), "Context notes", "The API is REST.", default);

        var list = result.Should().BeOfType<OkObjectResult>().Subject.Value as List<ProjectDocumentDto>;
        list.Should().HaveCount(2);
        var textDoc = list.Should().ContainSingle(d => d.Kind == ProjectDocumentKind.Text).Subject;
        textDoc.Title.Should().Be("Context notes");
        textDoc.Content.Should().BeNull();
        var fileDoc = list.Should().ContainSingle(d => d.Kind == ProjectDocumentKind.File).Subject;
        fileDoc.Title.Should().Be("diagram.svg", "the file must not inherit the text note's title");
        fileDoc.FileName.Should().Be("diagram.svg");

        (await db.ProjectDocuments.CountAsync(d => d.ProjectId == id)).Should().Be(2);
    }

    [Fact]
    public async Task CreateProjectDocuments_MultipleFiles_TitleAppliesToFirstOnly_SubsequentFallbackToFilenames()
    {
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        await db.SaveChangesAsync();

        var result = await Controller(db).CreateProjectDocuments(
            id,
            Files(("arch.png", "image/png", Bytes(8)), ("readme.txt", "text/plain", "hello"u8.ToArray())),
            "Main Architecture",
            null,
            default);

        var list = result.Should().BeOfType<OkObjectResult>().Subject.Value as List<ProjectDocumentDto>;
        list.Should().HaveCount(2);
        list![0].Title.Should().Be("Main Architecture");
        list[0].FileName.Should().Be("arch.png");
        list[1].Title.Should().Be("readme.txt");
        list[1].FileName.Should().Be("readme.txt");
    }

    [Fact]
    public async Task CreateProjectDocuments_EmptyFile_400AndWritesNothingAtAll()
    {
        // Partial success is the failure this endpoint is designed against: three of four files landing
        // looks like four landed. So validation is a full pass before any write.
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        await db.SaveChangesAsync();

        var result = await Controller(db).CreateProjectDocuments(
            id,
            Files(("good.md", "text/markdown", "# ok"u8.ToArray()), ("empty.md", "text/markdown", Bytes(0))),
            null, null, default);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await db.ProjectDocuments.CountAsync(d => d.ProjectId == id)).Should().Be(0,
            "the valid file must not be persisted when a later file in the same request is rejected");

        Directory.Exists(ProjectsController.GetDocumentDirectory(id)).Should().BeTrue("the directory is created eagerly");
        Directory.GetFiles(ProjectsController.GetDocumentDirectory(id)).Should().BeEmpty(
            "and no bytes from the valid file may be written before validation completes");
    }

    [Fact]
    public async Task CreateProjectDocuments_DisallowedExtension_400()
    {
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        await db.SaveChangesAsync();

        var result = await Controller(db).CreateProjectDocuments(
            id, Files(("payload.exe", "application/octet-stream", Bytes(10))), null, null, default);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await db.ProjectDocuments.CountAsync(d => d.ProjectId == id)).Should().Be(0);
    }

    [Fact]
    public async Task CreateProjectDocuments_OversizedFile_400()
    {
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        await db.SaveChangesAsync();

        // A 25 MB allocation per test is not worth it; the check is on the declared length, so a file
        // reporting a length above the cap exercises the same branch.
        var huge = new Mock<IFormFile>();
        huge.SetupGet(f => f.Length).Returns(ProjectDocument.MaxDocumentSizeBytes + 1);
        huge.SetupGet(f => f.FileName).Returns("huge.pdf");
        huge.SetupGet(f => f.ContentType).Returns("application/pdf");

        var collection = new FormFileCollection { huge.Object };
        var result = await Controller(db).CreateProjectDocuments(id, collection, null, null, default);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CreateProjectDocuments_MoreThanTenFiles_400()
    {
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        await db.SaveChangesAsync();

        var entries = Enumerable.Range(0, SessionsController.MaxAttachmentsPerMessage + 1)
            .Select(i => ($"note-{i}.md", "text/markdown", Bytes(3)))
            .ToArray();

        var result = await Controller(db).CreateProjectDocuments(id, Files(entries), null, null, default);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await db.ProjectDocuments.CountAsync(d => d.ProjectId == id)).Should().Be(0);
    }

    [Fact]
    public async Task CreateProjectDocuments_ExceedingLibraryCap_400AndWritesNothing()
    {
        // The cap is a property of the library, not the request, and it is what the prompt index was
        // sized for. Checked before any byte is written so concurrent uploads cannot each slip one past.
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        for (var i = 0; i < ProjectDocument.MaxDocumentsPerProject; i++)
        {
            db.ProjectDocuments.Add(new ProjectDocument
            {
                ProjectId = id,
                Title = $"Doc {i}",
                Kind = ProjectDocumentKind.Text,
                Content = "body"
            });
        }
        await db.SaveChangesAsync();

        var result = await Controller(db).CreateProjectDocuments(id, null, "one too many", "body", default);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await db.ProjectDocuments.CountAsync(d => d.ProjectId == id))
            .Should().Be(ProjectDocument.MaxDocumentsPerProject);
    }

    [Fact]
    public async Task CreateProjectDocuments_S3Failure_StillPersistsLocallyAndReturnsOk()
    {
        // The mirror is best-effort. An object store being down must not lose a document the user just
        // attached — the same contract as UploadJobAttachments.
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        await db.SaveChangesAsync();

        var storage = new Mock<IStorageService>();
        storage
            .Setup(s => s.UploadStreamAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SeaweedFS unreachable"));

        var result = await Controller(db, storage.Object).CreateProjectDocuments(
            id, Files(("notes.md", "text/markdown", "# hi"u8.ToArray())), null, null, default);

        var dto = (result.Should().BeOfType<OkObjectResult>().Subject.Value as List<ProjectDocumentDto>)![0];
        dto.S3Key.Should().NotBeNullOrEmpty(
            "the key is recorded even when the mirror failed, so a later pod can still look for the object");
        ProjectsController.FindLocalDocumentFile(
            ProjectsController.GetDocumentDirectory(id), Guid.Parse(dto.Id), dto.FileName).Should().NotBeNull();
    }

    [Fact]
    public async Task CreateProjectDocuments_S3Succeeds_UploadsUnderProjectScopedKey()
    {
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        await db.SaveChangesAsync();

        string? capturedKey = null;
        var storage = new Mock<IStorageService>();
        storage
            .Setup(s => s.UploadStreamAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, Stream _, string _, CancellationToken _) =>
            {
                capturedKey = key;
                return $"s3://bucket/{key}";
            });

        var dto = (await Controller(db, storage.Object).CreateProjectDocuments(
                id, Files(("spec.pdf", "application/pdf", Bytes(9))), null, null, default))
            .Should().BeOfType<OkObjectResult>().Subject.Value as List<ProjectDocumentDto>;

        capturedKey.Should().Be($"projects/{id}/documents/{dto![0].Id}/spec.pdf");
    }

    // ── read content ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetProjectDocumentContent_Text_ServesBodyFromRow()
    {
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        var doc = new ProjectDocument
        {
            ProjectId = id,
            Title = "API contract",
            Kind = ProjectDocumentKind.Text,
            Content = "POST /things creates a thing."
        };
        db.ProjectDocuments.Add(doc);
        await db.SaveChangesAsync();

        var controller = Controller(db);
        var result = await controller.GetProjectDocumentContent(id, doc.Id, false, default);

        var file = result.Should().BeOfType<FileStreamResult>().Subject;
        file.ContentType.Should().Be("text/markdown",
            "a pasted document is markdown in every case the dashboard produces, and the dashboard's viewer renders that inline");
        controller.Response.Headers.ContentDisposition.ToString().Should().StartWith("inline");

        using var reader = new StreamReader(file.FileStream!, System.Text.Encoding.UTF8);
        (await reader.ReadToEndAsync()).Should().Be("POST /things creates a thing.");
    }

    [Fact]
    public async Task GetProjectDocumentContent_File_ServesLocalBytes()
    {
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        await db.SaveChangesAsync();

        var png = new byte[] { 1, 2, 3, 4, 5 };
        var dto = (await Controller(db).CreateProjectDocuments(
                id, Files(("shot.png", "image/png", png)), null, null, default))
            .Should().BeOfType<OkObjectResult>().Subject.Value as List<ProjectDocumentDto>;

        var controller = Controller(db);
        var file = (await controller.GetProjectDocumentContent(id, Guid.Parse(dto![0].Id), false, default))
            .Should().BeOfType<FileStreamResult>().Subject;

        file.ContentType.Should().Be("image/png");
        controller.Response.Headers.ContentDisposition.ToString().Should().Contain("inline").And.Contain("shot.png");

        using var copy = new MemoryStream();
        await file.FileStream!.CopyToAsync(copy);
        copy.ToArray().Should().Equal(png);
    }

    [Fact]
    public async Task GetProjectDocumentContent_DownloadFlag_ForcesAttachmentDisposition()
    {
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        var doc = new ProjectDocument { ProjectId = id, Title = "Notes", Kind = ProjectDocumentKind.Text, Content = "body" };
        db.ProjectDocuments.Add(doc);
        await db.SaveChangesAsync();

        var controller = Controller(db);
        await controller.GetProjectDocumentContent(id, doc.Id, download: true, default);

        controller.Response.Headers.ContentDisposition.ToString().Should().StartWith("attachment",
            "download=true must override the inline rule even for a renderable type");
    }

    [Fact]
    public async Task GetProjectDocumentContent_NonRenderableType_IsAttachment()
    {
        // A zip served inline is a browser showing a wall of binary. The rule is the one both existing
        // content endpoints use, asserted here so the project path cannot quietly drop it.
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        await db.SaveChangesAsync();

        var dto = (await Controller(db).CreateProjectDocuments(
                id, Files(("bundle.zip", "application/zip", Bytes(12))), null, null, default))
            .Should().BeOfType<OkObjectResult>().Subject.Value as List<ProjectDocumentDto>;

        var controller = Controller(db);
        var file = (await controller.GetProjectDocumentContent(id, Guid.Parse(dto![0].Id), false, default))
            .Should().BeOfType<FileStreamResult>().Subject;

        file.ContentType.Should().Be("application/zip");
        controller.Response.Headers.ContentDisposition.ToString().Should().StartWith("attachment");
    }

    [Fact]
    public async Task GetProjectDocumentContent_FileWithNoBytesAnywhere_404()
    {
        // The row says a file exists; neither the local disk nor the object store has it. Returning an
        // empty 200 would render a broken image in the dashboard with nothing to explain it.
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        var doc = new ProjectDocument
        {
            ProjectId = id,
            Title = "vanished.pdf",
            Kind = ProjectDocumentKind.File,
            FileName = "vanished.pdf",
            ContentType = "application/pdf"
        };
        db.ProjectDocuments.Add(doc);
        await db.SaveChangesAsync();

        var result = await Controller(db).GetProjectDocumentContent(id, doc.Id, false, default);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetProjectDocumentContent_FileMissingLocally_FallsBackToS3()
    {
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        var doc = new ProjectDocument
        {
            ProjectId = id,
            Title = "from-s3.pdf",
            Kind = ProjectDocumentKind.File,
            FileName = "from-s3.pdf",
            ContentType = "application/pdf"
        };
        doc.S3Key = $"projects/{id}/documents/{doc.Id:N}/from-s3.pdf";
        db.ProjectDocuments.Add(doc);
        await db.SaveChangesAsync();

        var payload = new byte[] { 0x25, 0x50, 0x44, 0x46 };
        var storage = new Mock<IStorageService>();
        storage
            .Setup(s => s.DownloadStreamAsync(doc.S3Key!, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, CancellationToken _) => new MemoryStream(payload));

        var controller = Controller(db, storage.Object);
        var file = (await controller.GetProjectDocumentContent(id, doc.Id, false, default))
            .Should().BeOfType<FileStreamResult>().Subject;

        file.ContentType.Should().Be("application/pdf");
        using var copy = new MemoryStream();
        await file.FileStream!.CopyToAsync(copy);
        copy.ToArray().Should().Equal(payload);
    }

    [Fact]
    public async Task GetProjectDocumentContent_LocalFileWinsOverS3()
    {
        // Local-first is the deliberate difference from SessionsController: the local copy is always
        // written, the S3 mirror is not, so leading with S3 would let an outage break downloads that
        // need no network at all.
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        await db.SaveChangesAsync();

        var onDisk = new byte[] { 1, 1, 1 };
        var dto = (await Controller(db).CreateProjectDocuments(
                id, Files(("local.pdf", "application/pdf", onDisk)), null, null, default))
            .Should().BeOfType<OkObjectResult>().Subject.Value as List<ProjectDocumentDto>;

        var storage = new Mock<IStorageService>();
        storage
            .Setup(s => s.DownloadStreamAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, CancellationToken _) => new MemoryStream(new byte[] { 9, 9, 9 }));

        var controller = Controller(db, storage.Object);
        var file = (await controller.GetProjectDocumentContent(id, Guid.Parse(dto![0].Id), false, default))
            .Should().BeOfType<FileStreamResult>().Subject;

        using var copy = new MemoryStream();
        await file.FileStream!.CopyToAsync(copy);
        copy.ToArray().Should().Equal(onDisk);
        storage.Verify(s => s.DownloadStreamAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetProjectDocumentContent_DocumentFromAnotherProject_404()
    {
        // The ownership filter is in the query, so "not yours" and "does not exist" are the same answer.
        // A lookup by id alone would leak another project's filename and S3 key through a URL that looks
        // project-scoped.
        var mine = NewProjectId();
        var theirs = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.AddRange(NewProject(mine), NewProject(theirs));
        var doc = new ProjectDocument { ProjectId = theirs, Title = "Secret spec", Kind = ProjectDocumentKind.Text, Content = "x" };
        db.ProjectDocuments.Add(doc);
        await db.SaveChangesAsync();

        var result = await Controller(db).GetProjectDocumentContent(mine, doc.Id, false, default);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    // ── delete ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteProjectDocument_RemovesRowAndLocalFile()
    {
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        await db.SaveChangesAsync();

        var dto = (await Controller(db).CreateProjectDocuments(
                id, Files(("gone.md", "text/markdown", "# bye"u8.ToArray())), null, null, default))
            .Should().BeOfType<OkObjectResult>().Subject.Value as List<ProjectDocumentDto>;

        var docId = Guid.Parse(dto![0].Id);
        var directory = ProjectsController.GetDocumentDirectory(id);
        ProjectsController.FindLocalDocumentFile(directory, docId, "gone.md").Should().NotBeNull();

        var result = await Controller(db).DeleteProjectDocument(id, docId, default);

        result.Should().BeOfType<OkObjectResult>();
        (await db.ProjectDocuments.CountAsync(d => d.Id == docId)).Should().Be(0);
        ProjectsController.FindLocalDocumentFile(directory, docId, "gone.md").Should().BeNull(
            "the bytes are removed with the row, or the artifacts tree grows forever");
    }

    [Fact]
    public async Task DeleteProjectDocument_FreesASlotUnderTheLibraryCap()
    {
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        for (var i = 0; i < ProjectDocument.MaxDocumentsPerProject; i++)
        {
            db.ProjectDocuments.Add(new ProjectDocument
            {
                ProjectId = id,
                Title = $"Doc {i}",
                Kind = ProjectDocumentKind.Text,
                Content = "body"
            });
        }
        await db.SaveChangesAsync();

        var victim = await db.ProjectDocuments.FirstAsync(d => d.ProjectId == id);
        (await Controller(db).DeleteProjectDocument(id, victim.Id, default)).Should().BeOfType<OkObjectResult>();

        var after = await Controller(db).CreateProjectDocuments(id, null, "replacement", "body", default);
        after.Should().BeOfType<OkObjectResult>("a library that cannot be pruned cannot be used");
    }

    [Fact]
    public async Task DeleteProjectDocument_UnknownDocument_404()
    {
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        await db.SaveChangesAsync();

        var result = await Controller(db).DeleteProjectDocument(id, Guid.NewGuid(), default);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task DeleteProjectDocument_DocumentOwnedByAnotherProject_404AndSurvives()
    {
        var mine = NewProjectId();
        var theirs = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.AddRange(NewProject(mine), NewProject(theirs));
        var doc = new ProjectDocument { ProjectId = theirs, Title = "Theirs", Kind = ProjectDocumentKind.Text, Content = "x" };
        db.ProjectDocuments.Add(doc);
        await db.SaveChangesAsync();

        var result = await Controller(db).DeleteProjectDocument(mine, doc.Id, default);

        result.Should().BeOfType<NotFoundObjectResult>();
        (await db.ProjectDocuments.CountAsync(d => d.Id == doc.Id)).Should().Be(1,
            "a cross-project id must not be able to delete another project's document");
    }

    // ── the single-document read ────────────────────────────────────────────────

    [Fact]
    public async Task GetProjectDocument_IncludesTextBody()
    {
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        var doc = new ProjectDocument { ProjectId = id, Title = "Spec", Kind = ProjectDocumentKind.Text, Content = "the whole body" };
        db.ProjectDocuments.Add(doc);
        await db.SaveChangesAsync();

        var dto = (await Controller(db).GetProjectDocument(id, doc.Id, default))
            .Should().BeOfType<OkObjectResult>().Subject.Value as ProjectDocumentDto;

        dto!.Content.Should().Be("the whole body", "the single-document read is how an agent opens a text doc");
    }

    [Fact]
    public async Task GetProjectDocument_FileDocument_LeavesBodyNullAndPointsAtBytes()
    {
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        var doc = new ProjectDocument
        {
            ProjectId = id,
            Title = "diagram.png",
            Kind = ProjectDocumentKind.File,
            FileName = "diagram.png",
            ContentType = "image/png",
            SizeBytes = 8
        };
        doc.S3Key = $"projects/{id}/documents/{doc.Id:N}/diagram.png";
        db.ProjectDocuments.Add(doc);
        await db.SaveChangesAsync();

        var dto = (await Controller(db).GetProjectDocument(id, doc.Id, default))
            .Should().BeOfType<OkObjectResult>().Subject.Value as ProjectDocumentDto;

        dto!.Content.Should().BeNull("a file document has no body in the row to return");
        dto.DownloadUrl.Should().Be(ProjectDocumentIndexFormatter.BuildContentUrl(id, doc.Id));
    }

    // ── project deletion ────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteProject_RemovesDocumentRowsSoARecreatedSlugStartsEmpty()
    {
        // project_documents has no foreign key, matching every other project-scoped table here, which
        // means DeleteProject is the only thing that cleans it up. Skip that and a project deleted and
        // re-created under the same slug opens with the previous project's specs attached — not a leak of
        // deleted bytes, but a new project silently inheriting another project's context into every prompt.
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        db.ProjectDocuments.Add(new ProjectDocument { ProjectId = id, Title = "Old spec", Kind = ProjectDocumentKind.Text, Content = "x" });
        await db.SaveChangesAsync();

        var result = await Controller(db).DeleteProject(id);

        result.Should().BeOfType<OkObjectResult>();
        (await db.ProjectDocuments.CountAsync(d => d.ProjectId == id)).Should().Be(0,
            "the library is a project child like personas, jobs and messages, and nothing cascades here");
    }

    [Fact]
    public async Task DeleteProject_RemovesUploadedBytesFromDisk()
    {
        // The rows are the durable part, but the bytes live outside the database, so a project deletion
        // that only touched the table would leak a directory per deleted project forever.
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        await db.SaveChangesAsync();

        var dto = (await Controller(db).CreateProjectDocuments(
                id, Files(("gone.pdf", "application/pdf", Bytes(8))), null, null, default))
            .Should().BeOfType<OkObjectResult>().Subject.Value as List<ProjectDocumentDto>;

        var directory = ProjectsController.GetDocumentDirectory(id);
        Directory.Exists(directory).Should().BeTrue();
        Directory.GetFiles(directory).Should().HaveCount(1);

        await Controller(db).DeleteProject(id);

        Directory.Exists(directory).Should().BeFalse(
            "the guard in TryDeleteDocumentDirectory must not block the legitimate path, or this is a silent no-op");
    }

    [Fact]
    public async Task DeleteProject_LeavesOtherProjectsDocumentDirectoriesAlone()
    {
        // The recursive delete is scoped by slug. If the guard or the path were wrong in the loose
        // direction, deleting one project would take a sibling's library with it.
        var mine = NewProjectId();
        var theirs = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.AddRange(NewProject(mine), NewProject(theirs));
        await db.SaveChangesAsync();

        // GetDocumentDirectory creates the directory it names (the same create-on-read the existing
        // GetAttachmentDirectory helpers do), so the paths are captured BEFORE the delete. Asking for the
        // path afterwards would recreate the directory and report a successful cleanup as a failure.
        var mineDir = ProjectsController.GetDocumentDirectory(mine);
        var theirsDir = ProjectsController.GetDocumentDirectory(theirs);

        await Controller(db).CreateProjectDocuments(mine, Files(("mine.pdf", "application/pdf", Bytes(4))), null, null, default);
        await Controller(db).CreateProjectDocuments(theirs, Files(("theirs.pdf", "application/pdf", Bytes(4))), null, null, default);

        await Controller(db).DeleteProject(mine);

        Directory.Exists(mineDir).Should().BeFalse();
        Directory.Exists(theirsDir).Should().BeTrue("a project deletion is scoped to that project's directory");
        Directory.GetFiles(theirsDir).Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteProject_WithoutDocuments_Succeeds()
    {
        // The common case: a project that never attached anything. The cleanup must not run and must not
        // turn a project deletion into an error.
        var id = NewProjectId();
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(NewProject(id));
        await db.SaveChangesAsync();

        var result = await Controller(db).DeleteProject(id);

        result.Should().BeOfType<OkObjectResult>();
        (await db.Projects.CountAsync(p => p.Id == id)).Should().Be(0);
        (await db.ProjectDocuments.CountAsync(d => d.ProjectId == id)).Should().Be(0);
    }

    // ── path helpers ────────────────────────────────────────────────────────────

    [Fact]
    public void LocalDocumentPath_PutsIdFirstInNFormatSoThePrefixIsFixedLength()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var path = ProjectsController.LocalDocumentPath("/tmp/x", id, "a b.md");

        Path.GetFileName(path).Should().Be("11111111222233334444555555555555_a b.md");
        Path.GetFileName(path).IndexOf('_').Should().Be(32,
            "the separator position must not depend on where a guid's dashes fall");
    }

    [Fact]
    public void GetDocumentDirectory_IsProjectScopedAndSeparateFromJobs()
    {
        var dir = ProjectsController.GetDocumentDirectory("Acme-Project");

        dir.Should().EndWith(Path.Combine("projects", "acme-project", "documents"),
            "a project id and a job id must not share a namespace");
        Directory.Exists(dir).Should().BeTrue();
    }
}
