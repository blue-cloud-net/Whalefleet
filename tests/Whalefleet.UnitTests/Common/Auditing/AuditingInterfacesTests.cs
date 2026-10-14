using System.Reflection;
using Whalefleet.Core.Common;
using Whalefleet.Core.Common.Auditing;
using FluentAssertions;
using Xunit;

namespace Whalefleet.UnitTests.Common.Auditing;

/// <summary>
/// 审计接口的<b>结构契约</b>测试：防止后续重构悄悄破坏分层关系或放开写权限。
/// </summary>
public class AuditingInterfacesTests
{
    [Fact]
    public void ICreationAuditedObject_继承IHasCreationTime()
    {
        typeof(ICreationAuditedObject<Guid>)
            .Should().BeAssignableTo<IHasCreationTime>();
    }

    [Fact]
    public void IModificationAuditedObject_继承IHasModificationTime()
    {
        typeof(IModificationAuditedObject<Guid>)
            .Should().BeAssignableTo<IHasModificationTime>();
    }

    [Fact]
    public void IAuditedObject_同时继承创建与修改审计()
    {
        var t = typeof(IAuditedObject<Guid>);

        t.Should().BeAssignableTo<ICreationAuditedObject<Guid>>();
        t.Should().BeAssignableTo<IModificationAuditedObject<Guid>>();
    }

    [Fact]
    public void IHasDeletionTime_继承ISoftDelete()
    {
        typeof(IHasDeletionTime).Should().BeAssignableTo<ISoftDelete>();
    }

    [Fact]
    public void IDeletionAuditedObject_继承IHasDeletionTime()
    {
        typeof(IDeletionAuditedObject<Guid>).Should().BeAssignableTo<IHasDeletionTime>();
    }

    [Fact]
    public void IFullAuditedObject_继承完整审计与删除审计()
    {
        var t = typeof(IFullAuditedObject<Guid>);

        t.Should().BeAssignableTo<IAuditedObject<Guid>>();
        t.Should().BeAssignableTo<IDeletionAuditedObject<Guid>>();
        t.Should().BeAssignableTo<ISoftDelete>();
    }

    [Fact]
    public void 审计接口_只读不提供写权限()
    {
        var auditInterfaces = new[]
        {
            typeof(IHasCreationTime),
            typeof(IHasModificationTime),
            typeof(ISoftDelete),
            typeof(IHasDeletionTime),
            typeof(ICreationAuditedObject<Guid>),
            typeof(IModificationAuditedObject<Guid>),
            typeof(IDeletionAuditedObject<Guid>),
        };

        foreach (var t in auditInterfaces)
        {
            var setters = t.GetProperties()
                .Where(p => p.GetSetMethod() is not null)
                .Select(p => $"{t.Name}.{p.Name}");

            setters.Should().BeEmpty($"{t.Name} 是只读契约，不应暴露 setter");
        }
    }

    [Fact]
    public void 审计基类_审计属性均为internal_set()
    {
        var auditPropertyNames = new[]
        {
            "CreationTime", "CreatorId", "LastModificationTime", "LastModifierId",
            "IsDeleted", "DeletionTime", "DeleterId",
        };

        var baseTypes = new[]
        {
            typeof(CreationAuditedEntity<Guid, Guid>),
            typeof(AuditedEntity<Guid, Guid>),
            typeof(FullAuditedEntity<Guid, Guid>),
        };

        foreach (var baseType in baseTypes)
        {
            foreach (var name in auditPropertyNames)
            {
                var property = baseType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property is null)
                {
                    continue;
                }

                var setter = property.GetSetMethod(nonPublic: true);
                setter.Should().NotBeNull($"{baseType.Name}.{name} 应可写");
                setter!.IsPublic.Should().BeFalse(
                    $"{baseType.Name}.{name} 的 setter 不应对外公开，只能由基类 Mark* 方法写入");
            }
        }
    }

    [Fact]
    public void 审计基类_公开写入方法齐备()
    {
        typeof(CreationAuditedEntity<Guid, Guid>).GetMethod("MarkCreated").Should().NotBeNull();
        typeof(AuditedEntity<Guid, Guid>).GetMethod("MarkModified").Should().NotBeNull();
        typeof(FullAuditedEntity<Guid, Guid>).GetMethod("Delete").Should().NotBeNull();
        typeof(FullAuditedEntity<Guid, Guid>).GetMethod("Restore").Should().NotBeNull();
    }

    [Fact]
    public void 审计基类_写入方法均为public()
    {
        var methodNames = new[] { "MarkCreated", "MarkModified", "Delete", "Restore" };

        foreach (var type in new[]
        {
            typeof(CreationAuditedEntity<Guid, Guid>),
            typeof(AuditedEntity<Guid, Guid>),
            typeof(FullAuditedEntity<Guid, Guid>),
        })
        {
            foreach (var name in methodNames)
            {
                var method = type.GetMethod(name);
                if (method is null)
                {
                    continue;
                }

                method.IsPublic.Should().BeTrue($"{type.Name}.{name} 供仓储基类调用，须为 public");
            }
        }
    }

    [Fact]
    public void 审计基类_继承链正确()
    {
        typeof(CreationAuditedEntity<Guid, Guid>)
            .Should().BeDerivedFrom<Entity<Guid>>();

        typeof(AuditedEntity<Guid, Guid>)
            .Should().BeDerivedFrom<CreationAuditedEntity<Guid, Guid>>();

        typeof(FullAuditedEntity<Guid, Guid>)
            .Should().BeDerivedFrom<AuditedEntity<Guid, Guid>>();
    }

    [Fact]
    public void 审计基类_实现对应审计接口()
    {
        typeof(CreationAuditedEntity<Guid, Guid>)
            .Should().BeAssignableTo<ICreationAuditedObject<Guid>>();

        typeof(AuditedEntity<Guid, Guid>)
            .Should().BeAssignableTo<IAuditedObject<Guid>>();

        typeof(FullAuditedEntity<Guid, Guid>)
            .Should().BeAssignableTo<IFullAuditedObject<Guid>>();
    }

    [Fact]
    public void Core_未提供SoftDelete专用基类_纯软删除须自行实现ISoftDelete()
    {
        // 设计决策：只提供 CreationAudited / Audited / FullAudited 三级；
        // 纯软删除需求通过「实体自行实现 ISoftDelete」表达，不另开基类，避免继承层级膨胀。
        // 泛型类型的 Name 带元数后缀（形如 "AuditedEntity`2"），故按 ` 截断后比较。
        var entityTypeNames = typeof(Entity<Guid>).Assembly
            .GetExportedTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Select(t => t.Name.Split('`')[0])
            .ToHashSet(StringComparer.Ordinal);

        entityTypeNames.Should().NotContain("SoftDeleteEntity");
        entityTypeNames.Should().NotContain("Entity");
    }
}
