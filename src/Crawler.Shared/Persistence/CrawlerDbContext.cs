using Crawler.Shared.Entities;
using Crawler.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Shared.Persistence;

public sealed class CrawlerDbContext(DbContextOptions<CrawlerDbContext> options) : DbContext(options)
{
    // DbSet is EF's typed table/query entry point, similar to a model/table handle in a Node ORM.
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Page> Pages => Set<Page>();
    public DbSet<Link> Links => Set<Link>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    /// <summary>
    /// Defines how C# entities map to SQL tables, foreign keys, indexes, and stored enum strings.
    /// EF uses this model both for queries and for generating migrations.
    /// </summary>
    protected override void OnModelCreating(ModelBuilder model)
    {
        var job = model.Entity<Job>();
        job.ToTable("jobs", t =>
        {
            t.HasCheckConstraint("ck_jobs_max_depth", "max_depth BETWEEN 0 AND 5");
            t.HasCheckConstraint("ck_jobs_max_pages", "max_pages BETWEEN 1 AND 1000");
        });
        job.HasKey(x => x.Id);
        job.Property(x => x.ExtId).HasDefaultValueSql("gen_random_uuid()");
        job.HasIndex(x => x.ExtId).IsUnique();
        job.HasIndex(x => x.CreatedAt).IsDescending();
        job.Property(x => x.Status).HasConversion<string>();

        var page = model.Entity<Page>();
        page.ToTable("pages", t =>
        {
            t.HasCheckConstraint("ck_pages_depth", "depth >= 0");
            t.HasCheckConstraint("ck_pages_link_counts", "internal_link_count >= 0 AND total_link_count >= internal_link_count");
        });
        page.HasKey(x => x.Id);
        page.HasIndex(x => new { x.JobId, x.Url }).IsUnique();
        page.HasIndex(x => new { x.JobId, x.Status });
        page.HasIndex(x => x.ParentPageId);
        page.HasOne<Job>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        page.HasOne<Page>().WithMany().HasForeignKey(x => x.ParentPageId).OnDelete(DeleteBehavior.Restrict);
        page.Property(x => x.Status).HasConversion<string>();
        page.Property(x => x.LinkRatio).HasPrecision(7, 6);

        var link = model.Entity<Link>();
        link.ToTable("links");
        link.HasKey(x => new { x.SourcePageId, x.Url });
        link.HasOne<Page>().WithMany().HasForeignKey(x => x.SourcePageId).OnDelete(DeleteBehavior.Cascade);

        var outbox = model.Entity<OutboxMessage>();
        outbox.ToTable("crawl_outbox");
        outbox.HasKey(x => x.Id);
        outbox.HasIndex(x => new { x.CreatedAt, x.Id });
        outbox.Property(x => x.Destination).HasMaxLength(100);
        outbox.Property(x => x.Error).HasMaxLength(2000);
        outbox.Property(x => x.SourceMessageId).HasMaxLength(200);

        foreach (var entity in new[] { job.Metadata, page.Metadata, link.Metadata, outbox.Metadata })
            foreach (var property in entity.GetProperties())
                property.SetColumnName(ToSnakeCase(property.Name));

    }

    /// <summary>
    /// Converts property names such as ParentPageId to SQL column names such as parent_page_id.
    /// This keeps C# naming idiomatic without changing the database convention.
    /// </summary>
    private static string ToSnakeCase(string name) => string.Concat(name.Select((c, i) =>
        char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
