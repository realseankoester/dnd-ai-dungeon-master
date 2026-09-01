using System.ComponentModel;
using System.Net.Mime;
using DnDAIEngine.App;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OllamaSharp;

Console.WriteLine("--- Initializing D&D AI Engine Prototype");

// 1. Initialize Database & Seed Baseline Character
using var db = new DnDDbContext();

// Clean database state for fresh prototype runs
await db.Database.EnsureDeletedAsync();
await db.Database.EnsureCreatedAsync();

var session = new CampaignSession
{
    Title = "The Goblin Ambush",
    CurrentState = SessionState.InCombat,
    CurrentTurnIndex = 0,
    Characters = new List<Character>
    {
        new Character
        {
            Name = "Thorin",
            Class = "Fighter",
            CurrentHp = 12,
            MaxHp = 12,
            Stats = new CharacterStats { Strength = 16, Dexterity = 12, Constitution = 14 }
        }
    },
    // Set up Turn Order / Initiative Tracker
    Combatants = new List<Combatant>
    {
        new Combatant { Name = "Thorin", Initiative = 18, IsPlayer = true, CharacterId = 1 },
        new Combatant { Name = "Sneaky Goblin", Initiative = 12, IsPlayer = false }
    }
};
db.CampaignSessions.Add(session);
await db.SaveChangesAsync();

var activeSession = await db.CampaignSessions
.Include(s => s.Characters)
.Include(s => s.Combatants)
.Include(s => s.ChatHistoryMessages)
.FirstAsync();

var hero = activeSession.Characters.First();
var activeCombatant = activeSession.Combatants.OrderByDescending(c => c.Initiative).ElementAt(activeSession.CurrentTurnIndex);

Console.WriteLine("[DB Seed] Session Initialized. Combat Turn Order:");
foreach (var c in activeSession.Combatants.OrderByDescending(x => x.Initiative))
{
    Console.WriteLine($" - {c.Name} (Initiative: {c.Initiative})");
}

// 2. Set Up Semantic Kernel + Ollama
var kernelBuilder = Kernel.CreateBuilder();
kernelBuilder.AddOllamaChatCompletion("llama3.2", new Uri("http://localhost:11434"));

// Add C# Plugins
var healthPlugin = new HealthManagementPlugin(db);
var dicePlugin = new DicePlugin();
var turnPlugin = new TurnOrderPlugin(db, activeSession.Id);

kernelBuilder.Plugins.AddFromObject(healthPlugin, "HealthPlugin");
kernelBuilder.Plugins.AddFromObject(dicePlugin, "DicePlugin");
kernelBuilder.Plugins.AddFromObject(turnPlugin, "TurnPlugin");

var kernel = kernelBuilder.Build();
var chatService = kernel.GetRequiredService<IChatCompletionService>();

// 3. Build Chat Context
var chatHistory = new ChatHistory();
string systemPrompt = $"""
    You are a D&D 5e Dungeon Master.
    
    CURRENT COMBAT TURN: {activeCombatant.Name} (Is Player: {activeCombatant.IsPlayer}).
    ACTIVE HERO: {hero.Name} (ID: {hero.Id}, Class: {hero.Class}, HP: {hero.CurrentHp}/{hero.MaxHp}, AC: 15).
    
    RULES FOR COMBAT:
    1. If an attack/check occurs, call 'DicePlugin-roll_d20' first.
    2. If damage is rolled, call 'DicePlugin-roll_damage'.
    3. If damage/healing occurs, call 'HealthPlugin-modify_character_hp'.
    4. At the end of a combat turn, call 'TurnPlugin-advance_turn' to move to the next actor in initiative.
    """;

chatHistory.AddSystemMessage(systemPrompt);

// Save System Message to DB
db.ChatMessageEntities.Add(new ChatMessageEntity { CampaignSessionId = activeSession.Id, Role = "system", Content = systemPrompt });
await db.SaveChangesAsync();

// 4. Test Multi-Step Interaction
string testInput = "Thorin swings his longsword (+5 to hit, 1d8+3 damage) at the Sneaky Goblin (AC 13)!";
Console.WriteLine($"\n[Turn Action - {activeCombatant.Name}]: {testInput}");

chatHistory.AddUserMessage(testInput);
db.ChatMessageEntities.Add(new ChatMessageEntity { CampaignSessionId = activeSession.Id, Role = "user", Content = testInput});
await db.SaveChangesAsync();
Console.WriteLine("[Processing via Ollama / Llama 3.2...]");

PromptExecutionSettings settings = new()
{
    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
};

var response = await chatService.GetChatMessageContentAsync(chatHistory, settings, kernel);

// Save Assistant Narrative to DB
db.ChatMessageEntities.Add(new ChatMessageEntity { CampaignSessionId = activeSession.Id, Role = "assistant", Content = response.Content ?? "" });
await db.SaveChangesAsync();

// 5. Verification Output
await db.Entry(activeSession).ReloadAsync();

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine($"\n[DM Narrative]: {response.Content}");
Console.ResetColor();

var updatedTurnActor = activeSession.Combatants.OrderByDescending(c => c.Initiative).ElementAt(activeSession.CurrentTurnIndex);

Console.ForegroundColor = ConsoleColor.Yellow;
Console.WriteLine($"\n[PostgreSQL Verification]: Saved {db.ChatMessageEntities.Count()} chat messages. Next active turn actor is: '{updatedTurnActor.Name}'.");Console.ResetColor();

// --- Plugins ---

public class TurnOrderPlugin
{
    private readonly DnDDbContext _db;
    private readonly int _sessionId;

    public TurnOrderPlugin(DnDDbContext db, int sessionId)
    {
        _db = db;
        _sessionId = sessionId;
    }

    [KernelFunction("advanced_turn")]
    [Description("Advances the combat initiative order to the next actor's turn")]
    public async Task<string> AdvanceTurnAsync()
    {
        var session = await _db.CampaignSessions.Include(s => s.Combatants).FirstAsync(s => s.Id == _sessionId);
        int totalCombatants = session.Combatants.Count;

        session.CurrentTurnIndex = (session.CurrentTurnIndex + 1) % totalCombatants;
        await _db.SaveChangesAsync();

        var nextActor = session.Combatants.OrderByDescending(c => c.Initiative).ElementAt(session.CurrentTurnIndex);

        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine($"\n >>> [TURN PLUGIN] Advanced combat turn to index {session.CurrentTurnIndex}: {nextActor.Name}");
        Console.ResetColor();

        return $"Turn ended. Next actor to take an action is '{nextActor.Name}'.";
    }
}
public class HealthManagementPlugin
{
    private readonly DnDDbContext _db;
    public HealthManagementPlugin(DnDDbContext db) => _db = db;

    [KernelFunction("modify_character_hp")]
    [Description("Applies damage (negative value) or healing (positive value) to a character in the database.")]
    public async Task<string> ModifyCharacterHpAsync(
        [Description("The character's database ID")] int characterId,
        [Description("The HP delta: negative for damage, positive for healing")] int hpChange)
    {
        var character = await _db.Characters.FindAsync(characterId);
        if (character == null) return "Character not found.";

        character.CurrentHp = Math.Clamp(character.CurrentHp + hpChange, 0, character.MaxHp);
        await _db.SaveChangesAsync();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n >>> [HEALTH PLUGIN EXECUTED] Modified character {characterId} HP by {hpChange}. New Total: {character.CurrentHp}");
        Console.ResetColor();

        return $"Updated {character.Name}'s HP to {character.CurrentHp}/{character.MaxHp}.";
    }
}

public class DicePlugin
{
    private readonly Random _random = new();

    [KernelFunction("roll_d20")]
    [Description("Rolls a 20-sided die (d20) with a modifier, target DC/AC, and optional advantage or disadvantage.")]
    public string RollD20(
        [Description("Stat or skill modifier to add to the roll")] int modifier,
        [Description("Target Difficulty Class (DC) or Armor Class (AC) to beat")] int targetDc,
        [Description("Roll type: 'normal', 'advantage', or 'disadvantage'")] string rollType = "normal")
    {
        int roll1 = _random.Next(1, 21);
        int roll2 = _random.Next(1, 21);

        int baseRoll = rollType.ToLower() switch
        {
            "advantage" => Math.Max(roll1, roll2),
            "disadvantage" => Math.Min(roll1, roll2),
            _ => roll1
        };

        int total = baseRoll + modifier;
        bool isSuccess = total >= targetDc;
        string outcome = isSuccess ? "SUCCESS / HIT" : "FAILURE / MISS";

        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"\n >>> [DICE PLUGIN] Rolled d20 ({baseRoll}) + Mod ({modifier}) = {total} vs Target {targetDc} -> {outcome}");
        Console.ResetColor();

        return $"{outcome}! Base Roll: {baseRoll}, Total: {total} (vs DC/AC {targetDc}).";
    }

    [KernelFunction("roll_damage")]
    [Description("Rolls damage dice in standard notation like '1d6+2' or '2d8+3'.")]
    public string RollDamage(
        [Description("Dice formula in format 'NdX+M' or 'NdX' (e.g., '1d6+2', '2d8')")] string diceFormula)
    {
        try
        {
            string[] parts = diceFormula.ToLower().Split('+');
            string[] diceParts = parts[0].Split('d');

            int count = int.Parse(diceParts[0]);
            int sides = int.Parse(diceParts[1]);
            int modifier = parts.Length > 1 ? int.Parse(parts[1]) : 0;

            int total = 0;
            List<int> rolls = new();
            for (int i = 0; i < count; i++)
            {
                int r = _random.Next(1, sides + 1);
                rolls.Add(r);
                total += r;
            }
            total += modifier;

            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"\n >>> [DICE PLUGIN] Rolled {diceFormula}: [{string.Join(", ", rolls)}] + {modifier} = {total} damage");
            Console.ResetColor();

            return $"Damage Rolled ({diceFormula}): Total {total} damage.";
        }
        catch
        {
            return "Invalid dice formula format. Use '1d6+2' or '2d8'.";
        }
    }
}