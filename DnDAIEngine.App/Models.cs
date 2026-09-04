using System.ComponentModel.DataAnnotations;
using System.Dynamic;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DnDAIEngine.App;

// --- 1. Domain Enums & Models ---
public enum SessionState
{
    AwaitingPlayerAction,
    AwaitingDiceRoll,
    InCombat
}

public class CharacterStats
{
    public int Strength { get; set; } = 10;
    public int Dexterity { get; set; } = 10;
    public int Constitution { get; set; } = 10;
}

// --- 2. DataBase Entities ---
public class CampaignSession
{
    public int Id { get; set; }
    public string Title { get; set; } = "The Goblin Amubhs";
    public SessionState CurrentState { get; set; } = SessionState.AwaitingPlayerAction;
    public int CurrentTurnIndex { get; set; } = 0;
    public List<Character> Characters { get; set; } = new();
    public List<Combatant> Combatants { get; set; } = new();
    public List<ChatMessageEntity> ChatHistoryMessages { get; set; } = new();

}

public class Character
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Class { get; set; } = string.Empty;
    public int Level { get; set; } = 1;
    public int CurrentHp { get; set; }
    public int MaxHp { get; set; }

    // Stored as JSONB in PostgreSQL
    public CharacterStats  Stats { get; set; } = new();

    public int CampaignSessionId { get; set; }
    public CampaignSession CampaignSession { get; set; } = null!;
}

public class Combatant
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Initiative { get; set; }
    public bool IsPlayer { get; set; }
    public int? CharacterId { get; set; }
    public int CurrentHp { get; set; } = 7;
    public int MaxHp { get; set; } = 7;
    public CampaignSession CampaignSession { get; set; } = null!;
}

public class ChatMessageEntity
{
    public int Id { get; set; }
    public string Role { get; set; } = "user"; // "system", "user", "assistant", "tool"
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int CampaignSessionId { get; set; }
    public CampaignSession campaignSession { get; set; } = null!;
}

// --- 3. EF Core DBContext ---
public class DnDDbContext : DbContext
{
    public DbSet<CampaignSession> CampaignSessions => Set<CampaignSession>();
    public DbSet<Character> Characters => Set<Character>();
    public DbSet<Combatant> Combatants => Set<Combatant>();
    public DbSet<ChatMessageEntity> ChatMessageEntities => Set<ChatMessageEntity>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseNpgsql("Host=localhost;Database=dnd_db;Username=postgres;Password=postgres");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CampaignSession>()
            .Property(s => s.CurrentState)
            .HasConversion<string>();

        modelBuilder.Entity<Character>()
            .OwnsOne(c => c.Stats, builder => builder.ToJson());
    }
}