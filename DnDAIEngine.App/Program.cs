using System.ComponentModel;
using System.Net.Mime;
using DnDAIEngine.App;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OllamaSharp;

internal class Program
{
    private static async Task Main(string[] args)
    {
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
        var combatPlugin = new CombatEnginePlugin(db, activeSession.Id);
        var turnPlugin = new TurnOrderPlugin(db, activeSession.Id);

        kernelBuilder.Plugins.AddFromObject(combatPlugin, "CombatPlugin");
        kernelBuilder.Plugins.AddFromObject(turnPlugin, "TurnPlugin");

        var kernel = kernelBuilder.Build();
        var chatService = kernel.GetRequiredService<IChatCompletionService>();

        // 3. Build Chat Context
        var chatHistory = new ChatHistory();
        string systemPrompt = $"""
    You are a D&D 5e Dungeon Master assistant.
    
    INSTRUCTIONS:
    - Whenever a character attacks, call 'CombatPlugin-execute_attack'.
    - Required arguments: attackerName, targetName, attackModifier, targetAc, damageFormula.
    - Example: execute_attack(attackerName="Thorin", targetName="Sneaky Goblin", attackModifier=5, targetAc=13, damageFormula="1d8+3")
    """;

        chatHistory.AddSystemMessage(systemPrompt);

        // Save System Message to DB
        db.ChatMessageEntities.Add(new ChatMessageEntity { CampaignSessionId = activeSession.Id, Role = "system", Content = systemPrompt });
        await db.SaveChangesAsync();

        // 4. Test Multi-Step Interaction
        string testInput = "Thorin swings his longsword (+5 to hit, 1d8+3 damage) at the Sneaky Goblin (AC 13)!";
        Console.WriteLine($"\n[Turn Action - {activeCombatant.Name}]: {testInput}");

        chatHistory.AddUserMessage(testInput);
        db.ChatMessageEntities.Add(new ChatMessageEntity { CampaignSessionId = activeSession.Id, Role = "user", Content = testInput });
        await db.SaveChangesAsync();

        Console.WriteLine("[Processing via Ollama / Llama 3.2...]");

        PromptExecutionSettings settings = new() { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() };
        var response = await chatService.GetChatMessageContentAsync(chatHistory, settings, kernel);

        string rawContent = response.Content?.Trim() ?? string.Empty;
        string finalNarrative = rawContent;

        // Check if Llama 3.2 emitted a JSON tool string directly
        if (rawContent.Contains("execute_attack"))
        {
            try
            {
                // Extract raw JSON block if embedded in text
                int jsonStart = rawContent.IndexOf('{');
                int jsonEnd = rawContent.LastIndexOf('}');

                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    string jsonString = rawContent.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    using var doc = System.Text.Json.JsonDocument.Parse(jsonString);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("parameters", out var paramsObj))
                    {
                        string attacker = paramsObj.GetProperty("attackerName").GetString() ?? "Thorin";
                        string target = paramsObj.GetProperty("targetName").GetString() ?? "Sneaky Goblin";

                        // Parse integers whether Llama returned them as strings ("13") or numbers (13)
                        int attackMod = paramsObj.GetProperty("attackModifier").ValueKind == System.Text.Json.JsonValueKind.String
                            ? int.Parse(paramsObj.GetProperty("attackModifier").GetString()!)
                            : paramsObj.GetProperty("attackModifier").GetInt32();

                        int ac = paramsObj.GetProperty("targetAc").ValueKind == System.Text.Json.JsonValueKind.String
                            ? int.Parse(paramsObj.GetProperty("targetAc").GetString()!)
                            : paramsObj.GetProperty("targetAc").GetInt32();

                        string formula = paramsObj.GetProperty("damageFormula").GetString() ?? "1d8+3";

                        // 1. Run C# Engine
                        string combatResultText = await combatPlugin.ExecuteAttackAsync(attacker, target, attackMod, ac, formula);

                        // 2. Feed exact C# result back to LLM for final narration
                        chatHistory.AddUserMessage($"[SYSTEM RULE: Narrate the following combat result in 1-2 dramatic sentences without mentioning die math]: {combatResultText}");

                        var finalResponse = await chatService.GetChatMessageContentAsync(chatHistory, settings, kernel);

                        // Fallback to combatResultText directly if the LLM output is empty or whitespace
                        finalNarrative = string.IsNullOrWhiteSpace(finalResponse.Content) 
                            ? combatResultText 
                            : finalResponse.Content.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[Fallback Parser Exception]: {ex.Message}");
                Console.ResetColor();
            }
        }

        // Save Assistant Narrative to DB
        db.ChatMessageEntities.Add(new ChatMessageEntity { CampaignSessionId = activeSession.Id, Role = "assistant", Content = finalNarrative });
        await db.SaveChangesAsync();

        // 5. Verification Output
        await db.Entry(activeSession).ReloadAsync();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n[DM Narrative]: {finalNarrative}");
        Console.ResetColor();

        var updatedTurnActor = activeSession.Combatants.OrderByDescending(c => c.Initiative).ElementAt(activeSession.CurrentTurnIndex);

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n[PostgreSQL Verification]: Saved {db.ChatMessageEntities.Count()} chat messages. Next active turn actor is: '{updatedTurnActor.Name}'.");
        Console.ResetColor();
    }
}

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
public class CombatEnginePlugin
{
    private readonly DnDDbContext _db;
    private readonly int _sessionId;
    private readonly Random _random = new();

    public CombatEnginePlugin(DnDDbContext db, int sessionId)
    {
        _db = db;
        _sessionId = sessionId;
    }

    [KernelFunction("execute_attack")]
    [Description("Executes a full D&D 5e attack sequence against a target: rolls d20 vs AC, and ONLY if it hits, rolls damage and updates HP.")]
    public async Task<string> ExecuteAttackAsync(
        [Description("Attacker name")] string attackerName,
        [Description("Target name (e.g. 'Sneaky Goblin' or 'Thorin')")] string targetName,
        [Description("Attack modifier integer (e.g. 5)")] int attackModifier,
        [Description("Target AC integer (e.g. 13)")] int targetAc,
        [Description("Damage dice formula (e.g. '1d8+3')")] string damageFormula)
    {
        // 1. Roll Attack (d20 + modifier)
        int d20Roll = _random.Next(1, 21);
        int totalAttack = d20Roll + attackModifier;
        bool isHit = totalAttack >= targetAc;

        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"\n >>> [COMBAT ENGINE] {attackerName} Attack Roll: {d20Roll} + {attackModifier} = {totalAttack} vs AC {targetAc}");

        if (!isHit)
        {
            Console.WriteLine($" >>> [COMBAT ENGINE] Outcome: MISS! Skipping damage roll.");
            Console.ResetColor();
            return $"{attackerName}'s attack missed {targetName}! Rolled {totalAttack} vs AC {targetAc}.";
        }

        // 2. Roll Damage
        int damageTotal = ParseAndRollDamage(damageFormula);
        Console.WriteLine($" >>> [COMBAT ENGINE] Outcome: HIT! Rolled {damageFormula} = {damageTotal} damage.");

        // 3. Mutate DB State
        var session = await _db.CampaignSessions
            .Include(s => s.Combatants)
            .FirstOrDefaultAsync(s => s.Id == _sessionId);

        var targetCombatant = session?.Combatants.FirstOrDefault(c => c.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
        
        string hpStatusMessage = "";
        if (targetCombatant != null)
        {
            // If linked to a PC character, update Character entity
            if (targetCombatant.CharacterId != null)
            {
                var defenderChar = await _db.Characters.FindAsync(targetCombatant.CharacterId);
                if (defenderChar != null)
                {
                    defenderChar.CurrentHp = Math.Clamp(defenderChar.CurrentHp - damageTotal, 0, defenderChar.MaxHp);
                    hpStatusMessage = $"{defenderChar.Name} HP is now {defenderChar.CurrentHp}/{defenderChar.MaxHp}.";
                }         
            }
            else
            {
                // Update direct Combatant HP for monsters
                targetCombatant.CurrentHp = Math.Clamp(targetCombatant.CurrentHp - damageTotal, 0, targetCombatant.MaxHp);
                hpStatusMessage = $"{targetCombatant.Name} HP is now {targetCombatant.CurrentHp}/{targetCombatant.MaxHp}.";
            }

            await _db.SaveChangesAsync();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($" >>> [COMBAT ENGINE] Updated {targetName} HP in Postgres ({hpStatusMessage})");
        }

        Console.ResetColor();

        return $"{attackerName}'s attack HIT {targetName} for {damageTotal} damage! {hpStatusMessage}";
    }

    private int ParseAndRollDamage(string formula)
    {
        try
        {
            string[] parts = formula.ToLower().Split('+');
            string[] diceParts = parts[0].Split('d');
            int count = int.Parse(diceParts[0]);
            int sides = int.Parse(diceParts[1]);
            int modifier = parts.Length > 1 ? int.Parse(parts[1]) : 0;

            int total = modifier;
            for (int i = 0; i < count; i++) total += _random.Next(1, sides + 1);
            return total;
        }
        catch
        {
            return 4; // Fallback default damage
        }
    }
}