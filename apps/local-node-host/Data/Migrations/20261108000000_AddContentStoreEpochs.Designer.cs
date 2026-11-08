// Hand-maintained delta over the preceding migration target; the snapshot carries the whole model and the runtime parity test checks it.
using System;
using Harborline.Api.LocalNodeHost.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Migrations;

[DbContext(typeof(LocalNodeDbContext))]
[Migration("20261108000000_AddContentStoreEpochs")]
partial class AddContentStoreEpochs
{

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder.HasAnnotation("ProductVersion", "11.0.0-preview.5.26302.115");

        modelBuilder.Entity("Harborline.Api.LocalNodeHost.Data.ContentStore.ContentItemRow", b =>
            {
                b.Property<Guid>("ItemKey").HasColumnType("TEXT");
                b.Property<string>("ContentId").IsRequired().HasColumnType("TEXT");
                b.Property<long>("CreatedUtcTicks").HasColumnType("INTEGER");
                b.Property<Guid>("EpochId").HasColumnType("TEXT");
                b.Property<long>("Length").HasColumnType("INTEGER");
                b.Property<string>("MediaType").IsRequired().HasColumnType("TEXT");
                b.Property<byte[]>("NoncePrefix").IsRequired().HasColumnType("BLOB");
                b.Property<int>("SegmentSize").HasColumnType("INTEGER");
                b.Property<int>("State").HasColumnType("INTEGER");
                b.Property<string>("TenantId").IsRequired().HasColumnType("TEXT");
                b.Property<byte[]>("WrapNonce").IsRequired().HasColumnType("BLOB");
                b.Property<byte[]>("WrapTag").IsRequired().HasColumnType("BLOB");
                b.Property<byte[]>("WrappedDataKey").IsRequired().HasColumnType("BLOB");
                b.HasKey("ItemKey");
                b.HasIndex("TenantId", "EpochId");
                b.HasIndex("TenantId", "ContentId").IsUnique();
                b.ToTable("content_store_items");
            });

        modelBuilder.Entity("Harborline.Api.LocalNodeHost.Data.ContentStore.ContentStagedRow", b =>
            {
                b.Property<Guid>("ItemKey").HasColumnType("TEXT");
                b.Property<string>("ContentId").IsRequired().HasColumnType("TEXT");
                b.Property<long>("CreatedUtcTicks").HasColumnType("INTEGER");
                b.Property<Guid>("EpochId").HasColumnType("TEXT");
                b.Property<long>("Length").HasColumnType("INTEGER");
                b.Property<string>("MediaType").IsRequired().HasColumnType("TEXT");
                b.Property<byte[]>("NoncePrefix").IsRequired().HasColumnType("BLOB");
                b.Property<int>("SegmentSize").HasColumnType("INTEGER");
                b.Property<int>("State").HasColumnType("INTEGER");
                b.Property<string>("TenantId").IsRequired().HasColumnType("TEXT");
                b.Property<byte[]>("WrapNonce").IsRequired().HasColumnType("BLOB");
                b.Property<byte[]>("WrapTag").IsRequired().HasColumnType("BLOB");
                b.Property<byte[]>("WrappedDataKey").IsRequired().HasColumnType("BLOB");
                b.HasKey("ItemKey");
                b.HasIndex("TenantId", "EpochId");
                b.HasIndex("CreatedUtcTicks");
                b.ToTable("content_store_staged");
            });

        modelBuilder.Entity("Harborline.Api.LocalNodeHost.Data.ContentStore.ContentOwnerRow", b =>
            {
                b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
                b.Property<string>("Element").IsRequired().HasColumnType("TEXT");
                b.Property<Guid>("ItemKey").HasColumnType("TEXT");
                b.Property<string>("Record").IsRequired().HasColumnType("TEXT");
                b.HasKey("Id");
                b.HasIndex("ItemKey");
                b.ToTable("content_store_owners");
            });

        modelBuilder.Entity("Harborline.Api.LocalNodeHost.Data.ContentStore.ContentQuotaRow", b =>
            {
                b.Property<string>("TenantId").HasColumnType("TEXT");
                b.Property<bool>("TenantGrain").HasColumnType("INTEGER");
                b.Property<string>("Record").HasColumnType("TEXT");
                b.Property<long>("Bytes").HasColumnType("INTEGER");
                b.HasKey("TenantId", "TenantGrain", "Record");
                b.ToTable("content_store_quota");
            });

        modelBuilder.Entity("Harborline.Api.LocalNodeHost.Data.ContentStore.ContentReclamationRow", b =>
            {
                b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
                b.Property<string>("Cause").IsRequired().HasColumnType("TEXT");
                b.Property<string>("ContentId").IsRequired().HasColumnType("TEXT");
                b.Property<string>("Run").IsRequired().HasColumnType("TEXT");
                b.Property<string>("TenantId").IsRequired().HasColumnType("TEXT");
                b.HasKey("Id");
                b.ToTable("content_store_reclamations");
            });

        modelBuilder.Entity("Harborline.Api.LocalNodeHost.Data.ContentStore.ContentIntegrityRow", b =>
            {
                b.Property<string>("TenantId").HasColumnType("TEXT");
                b.Property<Guid>("ItemKey").HasColumnType("TEXT");
                b.Property<bool>("Active").HasColumnType("INTEGER");
                b.Property<long>("Count").HasColumnType("INTEGER");
                b.Property<long>("FirstSeenUtcTicks").HasColumnType("INTEGER");
                b.Property<long>("LastSeenUtcTicks").HasColumnType("INTEGER");
                b.Property<string>("OperationId").IsRequired().HasColumnType("TEXT");
                b.Property<string>("OwnerElement").IsRequired().HasColumnType("TEXT");
                b.Property<string>("OwnerRecord").IsRequired().HasColumnType("TEXT");
                b.Property<string>("PrincipalId").IsRequired().HasColumnType("TEXT");
                b.Property<int>("SegmentIndex").HasColumnType("INTEGER");
                b.Property<string>("TraceId").IsRequired().HasColumnType("TEXT");
                b.HasKey("TenantId", "ItemKey");
                b.ToTable("content_store_integrity");
            });

        modelBuilder.Entity("Harborline.Api.LocalNodeHost.Data.ContentStore.ContentEpochRow", b =>
            {
                b.Property<string>("TenantId").HasColumnType("TEXT");
                b.Property<Guid>("EpochId").HasColumnType("TEXT");
                b.HasKey("TenantId", "EpochId");
                b.ToTable("content_store_epochs");
            });

        modelBuilder.Entity("Harborline.Api.LocalNodeHost.Data.ContentStore.ContentOperationRow", b =>
            {
                b.Property<string>("IdempotencyKey").HasColumnType("TEXT");
                b.Property<string>("CommandId").IsRequired().HasColumnType("TEXT");
                b.Property<string>("Fingerprint").IsRequired().HasColumnType("TEXT");
                b.HasKey("IdempotencyKey");
                b.ToTable("content_store_operations");
            });

#pragma warning restore 612, 618
    }
}
