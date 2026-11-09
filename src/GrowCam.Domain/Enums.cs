namespace GrowCam.Domain;

/// <summary>How a strain starts flowering.</summary>
public enum SeedType
{
    Unknown,
    Autoflower,
    Photoperiod,
}

/// <summary>Stages of a grow, in chronological order.</summary>
public enum MilestoneType
{
    Sown,
    Sprouted,
    Vegetative,
    Flowering,
    Harvest,
}

public enum MilestoneStatus
{
    /// <summary>Reached and confirmed by the user.</summary>
    Confirmed,

    /// <summary>The AI thinks it is reached; waiting for the user.</summary>
    Suggested,

    /// <summary>The user said "not yet"; the AI keeps watching.</summary>
    Rejected,
}

public enum Phase
{
    Germination,
    Seedling,
    Vegetative,
    Flowering,
    Harvested,
}

/// <summary>How urgent an AI finding is. Deliberately only three levels.</summary>
public enum Severity
{
    Ok,
    Minor,
    Watch,
}

public enum DiaryKind
{
    Watered,
    Fertilized,
    Repotted,
    Trained,
    Note,
}

/// <summary>German display texts. Kept in one place so the UI stays consistent.</summary>
public static class Labels
{
    public static string Of(MilestoneType type) => type switch
    {
        MilestoneType.Sown => "Samen eingepflanzt",
        MilestoneType.Sprouted => "Keimling ist da",
        MilestoneType.Vegetative => "Wachstumsphase",
        MilestoneType.Flowering => "Erste Blüten",
        MilestoneType.Harvest => "Ernte",
        _ => type.ToString(),
    };

    /// <summary>The yes/no question shown when the AI suggests a milestone.</summary>
    public static string QuestionFor(MilestoneType type) => type switch
    {
        MilestoneType.Sprouted => "Sind die Keimlinge da?",
        MilestoneType.Vegetative => "Wachsen die Pflanzen schon richtig?",
        MilestoneType.Flowering => "Blühen die Pflanzen schon?",
        MilestoneType.Harvest => "Ist es Zeit für die Ernte?",
        _ => Of(type),
    };

    public static string Of(Phase phase) => phase switch
    {
        Phase.Germination => "Keimung",
        Phase.Seedling => "Keimling",
        Phase.Vegetative => "Wachstumsphase",
        Phase.Flowering => "Blütephase",
        Phase.Harvested => "Geerntet",
        _ => phase.ToString(),
    };

    public static string Of(Severity severity) => severity switch
    {
        Severity.Ok => "Alles gut",
        Severity.Minor => "Kleinigkeit",
        Severity.Watch => "Im Auge behalten",
        _ => severity.ToString(),
    };

    public static string Of(DiaryKind kind) => kind switch
    {
        DiaryKind.Watered => "Gegossen",
        DiaryKind.Fertilized => "Gedüngt",
        DiaryKind.Repotted => "Umgetopft",
        DiaryKind.Trained => "Training",
        DiaryKind.Note => "Notiz",
        _ => kind.ToString(),
    };

    public static string Of(SeedType type) => type switch
    {
        SeedType.Autoflower => "Autoflower – blüht von selbst",
        SeedType.Photoperiod => "Photoperiodisch – Licht muss für die Blüte umgestellt werden",
        _ => "Weiß ich nicht",
    };
}
