using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace Rosenvall.DevOps.Api.Migrations;

[DbContext(typeof(DevOpsStateDbContext))]
public class DevOpsStateDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder
            .HasAnnotation("ProductVersion", "10.0.8");

        modelBuilder.Entity("Rosenvall.DevOps.Api.DevOpsStateDocument", b =>
        {
            b.Property<string>("Id")
                .IsRequired();

            b.Property<string>("Json")
                .IsRequired();

            b.Property<DateTimeOffset>("UpdatedAt");

            b.HasKey("Id");

            b.ToTable("Documents");
        });
    }
}
