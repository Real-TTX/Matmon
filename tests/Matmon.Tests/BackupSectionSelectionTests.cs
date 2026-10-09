using Matmon.Core.Domain;
using Matmon.Host.Ui;

namespace Matmon.Tests;

/// <summary>
/// Every backup section has to be selectable in the three places that offer a choice (the job editor, the restore
/// page, the snapshot preview). Two sections - map images and MIBs - were added to the enum without being added to
/// the selection model, so the restore page gave them an empty field name and could never restore them, and saving a
/// job in the editor silently dropped them. These tests fail the day a section is added to the enum and forgotten
/// here.
/// </summary>
public class BackupSectionSelectionTests
{
    public static IEnumerable<object[]> EverySection() =>
        Enum.GetValues<WorkspaceBackupSection>()
            .Where(section => section is not WorkspaceBackupSection.None and not WorkspaceBackupSection.All)
            .Select(section => new object[] { section });

    [Theory]
    [MemberData(nameof(EverySection))]
    public void Every_section_is_selectable_and_survives_a_round_trip(WorkspaceBackupSection section)
    {
        var model = new BackupSectionSelectionModel();
        model.ApplySections(section);

        Assert.Equal(section, model.ToSections(defaultToAll: false));
        Assert.True(model.HasAnySelected());
    }

    [Theory]
    [MemberData(nameof(EverySection))]
    public void Every_section_has_a_property_named_after_it_because_that_is_the_posted_field(WorkspaceBackupSection section)
    {
        // The restore page posts "Input.Sections.<section>" - generated from the enum name - so the model must
        // have a bool of exactly that name for the binder to find.
        var property = typeof(BackupSectionSelectionModel).GetProperty(section.ToString());

        Assert.NotNull(property);
        Assert.Equal(typeof(bool), property!.PropertyType);
    }

    [Fact]
    public void Every_section_is_offered_in_the_catalog()
    {
        var offered = BackupSectionCatalog.GetChoices().Select(choice => choice.Section).Order().ToArray();
        var defined = EverySection().Select(row => (WorkspaceBackupSection)row[0]).Order().ToArray();

        Assert.Equal(defined, offered);
    }

    [Fact]
    public void All_is_exactly_the_union_of_the_sections()
    {
        var union = EverySection().Select(row => (WorkspaceBackupSection)row[0]).Aggregate(WorkspaceBackupSection.None, (all, section) => all | section);

        Assert.Equal(WorkspaceBackupSection.All, union);
    }

    [Fact]
    public void A_job_with_every_box_ticked_is_All()
    {
        // The editor's boxes all start ticked: saving without touching them has to mean "everything", including the
        // sections added after the editor was first written.
        Assert.Equal(WorkspaceBackupSection.All, new BackupSectionSelectionModel().ToSections());
    }

    [Fact]
    public void Unticking_one_box_removes_exactly_that_section()
    {
        var model = new BackupSectionSelectionModel { Mibs = false };

        Assert.Equal(WorkspaceBackupSection.All & ~WorkspaceBackupSection.Mibs, model.ToSections());
    }

    [Fact]
    public void Nothing_selected_means_nothing_on_the_restore_page_and_everything_in_the_editor()
    {
        var none = new BackupSectionSelectionModel();
        none.ApplySections(WorkspaceBackupSection.None);

        Assert.False(none.HasAnySelected());
        Assert.Equal(WorkspaceBackupSection.None, none.ToSections(defaultToAll: false));
        Assert.Equal(WorkspaceBackupSection.All, none.ToSections());
    }

    [Fact]
    public void The_restore_page_starts_from_nothing_because_an_unticked_box_is_never_posted()
    {
        // A browser does not send an unticked checkbox at all, so whatever the model defaults to is what an unticked
        // box MEANS. Defaulting to "everything" made it impossible to leave a section out of a restore: restoring just
        // the MIBs restored the users, the topology and the notification rules as well.
        var input = new Matmon.Host.Pages.BackupRestoreInput();

        Assert.False(input.Sections.HasAnySelected());
        Assert.Equal(WorkspaceBackupSection.None, input.Sections.ToSections(defaultToAll: false));
        Assert.Equal(WorkspaceBackupSection.None, BackupSectionSelectionModel.None().ToSections(defaultToAll: false));
    }

    [Fact]
    public void A_ticked_box_on_the_restore_page_adds_exactly_its_section()
    {
        // What the binder does with a posted "Input.Sections.Mibs=true" on top of the empty starting point.
        var input = new Matmon.Host.Pages.BackupRestoreInput();
        input.Sections.Mibs = true;

        Assert.Equal(WorkspaceBackupSection.Mibs, input.Sections.ToSections(defaultToAll: false));
    }
}
