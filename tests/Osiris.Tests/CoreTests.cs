using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Osiris.Core;
using Osiris.Tools.Database;

namespace Osiris.Tests;

public sealed class CoreTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("osiris-test").FullName;
    internal static readonly byte[] Key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData(".osiris/private/local-token")]
    [InlineData("docs/.git/config")]
    public void Resolve_refuses_paths_outside_or_hidden(string path) =>
        Assert.Throws<UnauthorizedAccessException>(() => new Workspace(root).Resolve(path));

    [Fact]
    public void Resolve_allows_workspace_paths() =>
        Assert.Equal(Path.Combine(root, "docs", "a.md"), new Workspace(root).Resolve("docs/a.md"));

    [Fact]
    public void Threads_round_trip_with_tool_calls_and_title()
    {
        var store = new ThreadStore(new Workspace(root));
        var id = Guid.NewGuid();
        store.Save(id, [
            new(ChatRole.User, "Write a note"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "write_file", new Dictionary<string, object?> { ["path"] = "a.md" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "Saved a.md")]),
        ]);

        var loaded = store.Load(id);
        Assert.Equal(3, loaded.Count);
        Assert.IsType<FunctionCallContent>(Assert.Single(loaded[1].Contents));
        Assert.Equal("Write a note", Assert.Single(store.List()).Title);
    }

    [Fact]
    public async Task Agent_calls_a_tool_streams_events_and_keeps_the_thread()
    {
        var model = new ScriptedModel(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "write_file", new Dictionary<string, object?> { ["path"] = "notes/a.md", ["content"] = "hello" })])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));
        var agent = new Agent(new Workspace(root), model, new LetterEmbeddings(), Key);
        var thread = Guid.NewGuid();

        var events = new List<RunEvent>();
        await foreach (var e in agent.RunAsync(thread, "Write hello to notes/a.md")) events.Add(e);

        Assert.Equal("hello", File.ReadAllText(Path.Combine(root, "notes", "a.md")));
        Assert.Collection(events,
            e => Assert.Equal("write_file", Assert.IsType<ToolCall>(e).ToolName),
            e => Assert.StartsWith("Saved notes/a.md", Assert.IsType<ToolResult>(e).Result),
            e => Assert.Equal("Done.", Assert.IsType<TextDelta>(e).Delta),
            e => Assert.IsType<Done>(e));
        Assert.Equal(4, agent.Threads.Load(thread).Count); // user, tool call, tool result, answer
        Assert.Contains(Path.GetFileName(root), model.LastOptions?.Instructions);
        Assert.Contains(model.LastOptions!.Tools!, t => t.Name == "read_file");
    }

    [Fact]
    public async Task Agent_hands_a_tool_error_back_to_the_model_and_goes_on()
    {
        var model = new ScriptedModel(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "no_such_tool")])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Sorry.")));
        var events = new List<RunEvent>();
        await foreach (var e in new Agent(new Workspace(root), model, new LetterEmbeddings(), Key).RunAsync(Guid.NewGuid(), "Hi")) events.Add(e);

        Assert.StartsWith("Error: no tool named", events.OfType<ToolResult>().Single().Result);
        Assert.Equal("Sorry.", events.OfType<TextDelta>().Single().Delta);
        Assert.IsType<Done>(events[^1]);
    }

    [Fact]
    public async Task Agent_reports_a_model_failure_as_an_event()
    {
        var events = new List<RunEvent>();
        await foreach (var e in new Agent(new Workspace(root), new ScriptedModel(), new LetterEmbeddings(), Key).RunAsync(Guid.NewGuid(), "Hi")) events.Add(e);
        Assert.IsType<RunError>(Assert.Single(events));
    }

    [Fact]
    public void Memory_remembers_and_forgets_newest_first()
    {
        var memories = new Memories(new Workspace(root));
        Assert.StartsWith("Saved", memories.Remember("Invoices are due in 14 days."));
        memories.Remember("The client prefers short answers.");

        Assert.Equal(["The client prefers short answers.", "Invoices are due in 14 days."], memories.List().Select(m => m.Content));
        Assert.Equal("No such memory.", memories.Forget("../../secret"));
        Assert.Equal("Forgotten.", memories.Forget(memories.List()[0].Id));
        Assert.Equal("Invoices are due in 14 days.", Assert.Single(memories.List()).Content);
        Assert.Contains("Invoices are due in 14 days.", memories.Prompt());
    }

    [Fact]
    public void Workspace_skills_win_over_bundled_ones()
    {
        var skills = new Skills(new Workspace(root));
        Assert.Contains(skills.List(), s => s.Name == "skill-creator"); // bundled

        Assert.StartsWith("Skill \"invoice\" saved", skills.SaveSkill("invoice", "Issuing an invoice.", "1. Ask for the amount."));
        Assert.StartsWith("Skill \"skill-creator\" saved", skills.SaveSkill("skill-creator", "Ours.", "Our way."));
        Assert.StartsWith("Error", skills.SaveSkill("../evil", "x", "y"));

        Assert.Equal("1. Ask for the amount.", skills.LoadSkill("invoice"));
        Assert.Equal("Our way.", skills.LoadSkill("skill-creator"));
        Assert.Contains("- invoice: Issuing an invoice.", skills.Prompt());
        Assert.Null(Skills.Parse("no frontmatter"));
    }

    [Fact]
    public async Task Agent_tells_the_model_its_memory_and_skills_and_gives_it_their_tools()
    {
        new Memories(new Workspace(root)).Remember("The client is ACME.");
        var model = new ScriptedModel(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Ok.")));
        await foreach (var _ in new Agent(new Workspace(root), model, new LetterEmbeddings(), Key).RunAsync(Guid.NewGuid(), "Hi")) { }

        Assert.Contains("The client is ACME.", model.LastOptions!.Instructions);
        Assert.Contains("- skill-creator:", model.LastOptions.Instructions);
        Assert.Contains(model.LastOptions.Tools!, t => t.Name == "remember");
        Assert.Contains(model.LastOptions.Tools!, t => t.Name == "load_skill");
    }

    [Fact]
    public async Task Knowledge_follows_the_files_as_they_are_added_changed_and_removed()
    {
        var embeddings = new LetterEmbeddings();
        var knowledge = new Knowledge(new Workspace(root), embeddings);
        var dir = Directory.CreateDirectory(Path.Combine(root, "knowledge", "clients"));
        File.WriteAllText(Path.Combine(root, "knowledge", "prices.md"), "# Prices\n\nAn hour of work costs 900 CZK.");
        File.WriteAllText(Path.Combine(dir.FullName, "acme.md"), "ACME pays invoices late, zzz zzz.");

        Assert.Equal("knowledge/prices.md", (await knowledge.SearchAsync("hour of work costs", 1))[0].Path);
        var embedded = embeddings.Texts;
        await knowledge.SearchAsync("anything", 1);
        Assert.Equal(embedded + 1, embeddings.Texts); // unchanged files aren't embedded again, only the query

        File.WriteAllText(Path.Combine(root, "knowledge", "prices.md"), "# Prices\n\nAn hour costs 1000 CZK now.");
        Assert.Contains("1000 CZK", (await knowledge.SearchAsync("hour costs", 1))[0].Text);

        File.Delete(Path.Combine(dir.FullName, "acme.md"));
        Assert.All(await knowledge.SearchAsync("zzz", 10), h => Assert.Equal("knowledge/prices.md", h.Path));
        // A new instance (a restart) reads the index from disk instead of embedding again.
        var before = embeddings.Texts;
        await new Knowledge(new Workspace(root), embeddings).SearchAsync("hour", 1);
        Assert.Equal(before + 1, embeddings.Texts);

        // Another embedding model reindexes everything.
        var other = new LetterEmbeddings("other-model");
        Assert.Equal("knowledge/prices.md", (await new Knowledge(new Workspace(root), other).SearchAsync("hour", 1))[0].Path);
        Assert.Equal(2, other.Texts); // the one chunk left, and the query
    }

    [Fact]
    public void Knowledge_splits_paragraphs_into_bounded_chunks()
    {
        var chunks = Knowledge.Split("a\r\n\r\nb\n\n" + new string('x', 2500));
        Assert.Equal(["a\n\nb", new string('x', 1000), new string('x', 1000), new string('x', 500)], chunks);
    }

    internal static ChatResponse Says(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    static string Plan(params string[] steps) => $$"""{"rationale":"because","steps":[{{string.Join(",", steps)}}]}""";

    static string Step(string id, string pattern = "ReAct", string deps = "", bool destructive = false, string extra = "") =>
        $$"""{"id":"{{id}}","goal":"do {{id}}","pattern":"{{pattern}}","dependsOn":[{{deps}}],"destructive":{{(destructive ? "true" : "false")}}{{extra}}}""";

    [Fact]
    public void Workflow_plans_are_validated_and_put_in_dependency_order()
    {
        PlanStep S(string id, params string[] deps) => new(id, id, "ReAct", deps, false);
        Assert.Equal(["a", "b", "c"], Workflows.Validate([S("c", "b"), S("a"), S("b", "a")]).Select(s => s.Id));
        Assert.Throws<InvalidOperationException>(() => Workflows.Validate([S("a", "b"), S("b", "a")]));
        Assert.Throws<InvalidOperationException>(() => Workflows.Validate([S("a", "x")]));
        Assert.Throws<InvalidOperationException>(() => Workflows.Validate([S("a"), S("a")]));
        Assert.Throws<InvalidOperationException>(() => Workflows.Validate([new("m", "m", "MapReduce", [], false)]));
        Assert.Throws<InvalidOperationException>(() => Workflows.Validate([new("A B", "x", "ReAct", [], false)]));
    }

    [Fact]
    public async Task Workflow_runs_steps_in_order_and_hands_results_on()
    {
        var goals = new List<string>();
        var model = new ScriptedModel(Says(Plan(Step("a"), Step("b", deps: "\"a\""))), Says("All done."));
        var workflows = new Workflows(new Workspace(root), model, new Questions(new Workspace(root)), (goal, _) => { lock (goals) goals.Add(goal); return Task.FromResult($"output of {goals.Count}"); });

        var answer = await workflows.RunAsync(Guid.NewGuid(), "Do a then b", false);

        Assert.Equal("do a", goals[0]);
        Assert.Contains("output of 1", goals[1]); // b sees a's result
        Assert.StartsWith("All done.", answer);
        Assert.Contains("a: success, b: success", answer);
    }

    [Fact]
    public async Task Workflow_waits_for_the_users_approval_of_steps_that_change_data()
    {
        var runs = 0;
        var questions = new Questions(new Workspace(root));
        var model = new ScriptedModel(Says(Plan(Step("read"), Step("write", deps: "\"read\"", destructive: true))), Says("Read only."));
        var workflows = new Workflows(new Workspace(root), model, questions, (_, _) => Task.FromResult($"run {++runs}"));
        var thread = Guid.NewGuid();

        var asked = await workflows.RunAsync(thread, "Read and rewrite the notes", false);
        var question = questions.Open(thread)!;
        Assert.Contains(question.Id.ToString(), asked);
        Assert.Equal("do write", Assert.Single(question.Fields).Label);
        Assert.Equal(0, runs);
        Assert.StartsWith("The user hasn't answered", await workflows.RunAsync(thread, "", true));

        questions.Prepare(thread, "ne"); // one yes/no field: plain text answers it
        var answer = await workflows.RunAsync(thread, "", true);
        Assert.Equal(1, runs);
        Assert.Contains("read: success, write: rejected", answer);
        Assert.StartsWith("Not done: do write (rejected)", answer);
        Assert.Equal("No workflow waits for approval.", await workflows.RunAsync(thread, "", true));
    }

    [Fact]
    public void Questions_accept_only_answers_that_fit_their_fields()
    {
        var questions = new Questions(new Workspace(root));
        var thread = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => questions.Create(thread, "Pick", [new("color", "Color", "select", true)])); // no options
        var q = questions.Create(thread, "Order", [
            new("color", "Color", "select", true, [new("red", "Red"), new("blue", "Blue")]),
            new("count", "Count", "number", true),
            new("note", "Note", "text", false)]);
        Assert.Equal(q.Id, questions.Create(thread, "Another", [new("x", "X", "text", true)]).Id); // one open at a time

        Assert.Equal("Hello", questions.Prepare(Guid.NewGuid(), "Hello")); // nothing asked there
        Assert.Throws<QuestionException>(() => questions.Prepare(thread, "red")); // several fields: the form
        string Form(string values) => Questions.AnswerPrefix + "{\"id\":\"" + q.Id + "\",\"values\":{" + values + "}}";
        Assert.Throws<QuestionException>(() => questions.Prepare(thread, Form("\"color\":\"green\",\"count\":2")));
        Assert.Throws<QuestionException>(() => questions.Prepare(thread, Form("\"color\":\"red\"")));

        var answer = questions.Prepare(thread, Form("\"color\":\"red\",\"count\":\"3\""));
        Assert.Contains("""{"color":"red","count":3}""", answer);
        Assert.Equal("Answered: red · 3", Questions.Display(answer));
        Assert.Null(questions.Open(thread));
        Assert.Throws<QuestionException>(() => questions.Prepare(thread, Form("\"color\":\"blue\",\"count\":1"))); // answered once
        Assert.NotNull(questions.Take(thread, q.Id));
        Assert.Null(questions.Take(thread, q.Id)); // one answer approves one action
    }

    [Fact]
    public void Questions_take_a_choice_by_its_label_or_number_and_can_be_cancelled()
    {
        var questions = new Questions(new Workspace(root));
        var thread = Guid.NewGuid();
        questions.Create(thread, "Size?", [new("size", "Size", "select", true, [new("s", "Small"), new("l", "Large")])]);
        Assert.Contains("""{"size":"l"}""", questions.Prepare(thread, "large"));
        questions.Create(thread, "Size?", [new("size", "Size", "select", true, [new("s", "Small"), new("l", "Large")])]);
        Assert.Contains("""{"size":"s"}""", questions.Prepare(thread, "1"));
        questions.Create(thread, "Delete it?", [new("ok", "Delete", "boolean", true)]);
        Assert.Equal("Cancelled.", Questions.Display(questions.Prepare(thread, "/cancel")));
    }

    [Fact]
    public async Task Agent_stops_after_ask_user_and_hands_the_answer_to_the_model()
    {
        var ask = new FunctionCallContent("q1", "ask_user", new Dictionary<string, object?>
        {
            ["title"] = "Which client?",
            ["fields"] = JsonSerializer.SerializeToElement(new[] { new { name = "client", label = "Client", type = "text", required = true } }),
        });
        var model = new ScriptedModel(new ChatResponse(new ChatMessage(ChatRole.Assistant, [ask])), Says("Invoice for ACME."));
        var agent = new Agent(new Workspace(root), model, new LetterEmbeddings(), Key);
        var thread = Guid.NewGuid();

        var first = new List<RunEvent>();
        await foreach (var e in agent.RunAsync(thread, "Make an invoice")) first.Add(e);
        Assert.IsType<Done>(first[^1]); // stopped without calling the model again (it has one answer left)
        Assert.Equal("Which client?", agent.Questions.Open(thread)?.Title);

        var second = new List<RunEvent>();
        await foreach (var e in agent.RunAsync(thread, "ACME")) second.Add(e);
        Assert.Equal("Invoice for ACME.", second.OfType<TextDelta>().Single().Delta);
        Assert.Contains("""{"client":"ACME"}""", agent.Threads.Load(thread).Last(m => m.Role == ChatRole.User).Text);
    }

    [Fact]
    public async Task Workflow_jev_repairs_an_artifact_until_it_passes_its_check()
    {
        var model = new ScriptedModel(
            Says(Plan(Step("schema", "JEV", extra: ",\"verifier\":\"json\""))),
            Says("""{"items":["Valid JSON with a name"]}"""),
            Says("{ not json"),
            Says("```json\n{\"name\":\"x\"}\n```"),
            Says("""{"accepted":true,"feedback":""}"""));
        var workflows = new Workflows(new Workspace(root), model, new Questions(new Workspace(root)), (_, _) => throw new InvalidOperationException("no ReAct here"));

        var answer = await workflows.RunAsync(Guid.NewGuid(), "Make a JSON with a name", false);
        Assert.StartsWith("{\"name\":\"x\"}", answer);
        Assert.Contains("schema: success", answer);
    }

    [Fact]
    public async Task Workflow_falls_back_to_one_agent_step_when_the_plan_is_broken()
    {
        var model = new ScriptedModel(Says("this is no plan"));
        var workflows = new Workflows(new Workspace(root), model, new Questions(new Workspace(root)), (goal, _) => Task.FromResult($"handled: {goal}"));
        var answer = await workflows.RunAsync(Guid.NewGuid(), "Whatever", false);
        Assert.StartsWith("handled: Whatever", answer);
    }

    [Fact]
    public async Task Agent_offers_run_workflow_in_a_chat()
    {
        var model = new ScriptedModel(Says("Ok."));
        await foreach (var _ in new Agent(new Workspace(root), model, new LetterEmbeddings(), Key).RunAsync(Guid.NewGuid(), "Hi")) { }
        Assert.Contains(model.LastOptions!.Tools!, t => t.Name == "run_workflow");
    }

    void Rule(string name, string text)
    {
        Directory.CreateDirectory(Path.Combine(root, ".osiris", "guards"));
        File.WriteAllText(Path.Combine(root, ".osiris", "guards", $"{name}.md"), $"---\ndescription: test\n---\n\n{text}\n");
    }

    async Task<List<RunEvent>> Run(Agent agent, Guid thread, string message)
    {
        var events = new List<RunEvent>();
        await foreach (var e in agent.RunAsync(thread, message)) events.Add(e);
        return events;
    }

    [Fact]
    public async Task Guards_refuse_a_message_breaking_the_rules_without_asking_the_model()
    {
        Rule("no-prices", "Never tell prices.");
        var model = new ScriptedModel(Says("YES"));
        var agent = new Agent(new Workspace(root), model, new LetterEmbeddings(), Key);
        var events = await Run(agent, Guid.NewGuid(), "What does it cost?");
        Assert.Equal(Guards.Refusal, Assert.IsType<TextDelta>(events[0]).Delta);
        Assert.IsType<Done>(events[1]);
    }

    [Fact]
    public async Task Guards_replace_an_answer_breaking_the_rules_in_the_history_too()
    {
        Rule("no-prices", "Never tell prices.");
        var model = new ScriptedModel(Says("NO"), Says("It costs 900 CZK."), Says("YES"));
        var agent = new Agent(new Workspace(root), model, new LetterEmbeddings(), Key);
        var thread = Guid.NewGuid();
        var events = await Run(agent, thread, "Tell me about the service");

        Assert.Equal([Guards.Refusal], events.OfType<TextDelta>().Select(d => d.Delta)); // the answer never showed
        Assert.Equal(Guards.Refusal, agent.Threads.Load(thread).Last().Text);
        Assert.Contains("Never tell prices.", model.LastOptions!.Instructions);
    }

    [Fact]
    public async Task Guards_let_an_answer_within_the_rules_through_whole()
    {
        Rule("polite", "Be polite.");
        var model = new ScriptedModel(Says("NO"), Says("Gladly."), Says("NO"));
        var events = await Run(new Agent(new Workspace(root), model, new LetterEmbeddings(), Key), Guid.NewGuid(), "Hi");
        Assert.Equal("Gladly.", events.OfType<TextDelta>().Single().Delta);
        Assert.Contains("# BINDING RULES", model.LastOptions!.Instructions);
    }

    [Fact]
    public async Task Guards_withdraw_a_question_breaking_the_rules()
    {
        Rule("no-passwords", "Never ask for passwords.");
        var ask = new FunctionCallContent("q1", "ask_user", new Dictionary<string, object?>
        {
            ["title"] = "Your password?",
            ["fields"] = JsonSerializer.SerializeToElement(new[] { new { name = "password", label = "Password", type = "text", required = true } }),
        });
        var model = new ScriptedModel(Says("NO"), new ChatResponse(new ChatMessage(ChatRole.Assistant, [ask])), Says("YES"), Says("I won't ask that."), Says("NO"));
        var agent = new Agent(new Workspace(root), model, new LetterEmbeddings(), Key);
        var thread = Guid.NewGuid();
        var events = await Run(agent, thread, "Log me in");

        Assert.StartsWith("The question breaks the rules", events.OfType<ToolResult>().Single().Result);
        Assert.Null(agent.Questions.Open(thread));
        Assert.Equal(QuestionState.Cancelled, agent.Questions.Latest(thread)!.State);
        Assert.Equal("I won't ask that.", events.OfType<TextDelta>().Single().Delta);
    }

    [Fact]
    public void Secrets_are_sealed_and_filled_only_for_their_hosts()
    {
        var secrets = new Secrets(new Workspace(root), Key);
        secrets.Put("CRM_TOKEN", "s3cret-value", ["api.crm.example", "*.crm.example"], "The CRM");
        Assert.DoesNotContain("s3cret-value", File.ReadAllText(Path.Combine(root, ".osiris", "secrets", "CRM_TOKEN.json")));
        Assert.Equal(["api.crm.example", "*.crm.example"], secrets.List().Single().AllowedHosts);
        Assert.Contains("{{secret:CRM_TOKEN}} for api.crm.example, *.crm.example: The CRM", secrets.Prompt());

        var (filled, used) = secrets.Fill(new Dictionary<string, string> { ["h"] = "Bearer {{secret:CRM_TOKEN}}" }, "eu.crm.example");
        Assert.Equal("Bearer s3cret-value", filled["h"]);
        Assert.Equal("token [hidden]", Secrets.Redact("token s3cret-value", used));
        Assert.Throws<SecretException>(() => secrets.Fill(new Dictionary<string, string> { ["h"] = "{{secret:CRM_TOKEN}}" }, "evil.example"));
        Assert.Throws<SecretException>(() => secrets.Fill(new Dictionary<string, string> { ["h"] = "{{secret:NOPE}}" }, "api.crm.example"));
        Assert.Throws<SecretException>(() => Secrets.HostOf("https://{{secret:CRM_TOKEN}}.example/x"));
        Assert.Equal("api.crm.example", Secrets.HostOf("https://api.crm.example/v1?key={{secret:CRM_TOKEN}}"));

        secrets.Put("CRM_TOKEN", null, ["api.crm.example"]); // only the hosts change
        Assert.Equal("s3cret-value", secrets.Fill(new Dictionary<string, string> { ["h"] = "{{secret:CRM_TOKEN}}" }, "api.crm.example").Filled["h"]);
        Assert.Throws<SecretException>(() => new Secrets(new Workspace(root), new byte[32]).Fill(new Dictionary<string, string> { ["h"] = "{{secret:CRM_TOKEN}}" }, "api.crm.example"));
        Assert.Throws<SecretException>(() => secrets.Put("lower", "x", ["a.example"]));
        Assert.Throws<SecretException>(() => secrets.Put("NO_HOSTS", "x", []));
        Assert.True(secrets.Delete("CRM_TOKEN"));
    }

    static Dictionary<string, JsonElement> Row(object values) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values))!;

    [Fact]
    public void Database_keeps_typed_rows_with_references_and_unique_values()
    {
        var db = new Database(new Workspace(root));
        db.CreateTable("contacts", "People we work with", [new("name", "text", Required: true), new("email", "text", Unique: true), new("vip", "boolean")]);
        db.CreateTable("deals", "", [new("contact_id", "integer", References: "contacts"), new("amount", "real"), new("due", "date")]);

        var ids = db.Insert("contacts", [Row(new { name = "Ann", email = "ann@example.com", vip = true }), Row(new { name = "Bob", email = "bob@example.com" })]);
        Assert.Equal([1L, 2L], ids);
        Assert.Throws<DataException>(() => db.Insert("contacts", [Row(new { name = "Ann 2", email = "ann@example.com" })])); // unique
        Assert.Throws<DataException>(() => db.Insert("contacts", [Row(new { email = "x@example.com" })])); // required
        Assert.Throws<DataException>(() => db.Insert("contacts", [Row(new { name = "Eve", id = 9 })])); // automatic column
        Assert.Throws<DataException>(() => db.Insert("deals", [Row(new { contact_id = 99 })])); // no such contact
        Assert.Throws<DataException>(() => db.Insert("deals", [Row(new { due = "31.12.2026" })]));
        db.Insert("deals", [Row(new { contact_id = 1, amount = "1500,50", due = "2026-12-31" })]);

        Assert.Equal(1, db.Update("contacts", Row(new { id = 2 }), Row(new { vip = "true" })));
        Assert.Throws<DataException>(() => db.Update("contacts", [], Row(new { vip = false }))); // where is required
        var result = db.Query("SELECT c.name, d.amount FROM contacts c JOIN deals d ON d.contact_id = c.id;");
        Assert.Equal(["name", "amount"], result.Columns);
        Assert.Equal(["Ann", 1500.5], result.Rows.Single());
        Assert.Equal(2L, db.Query("select count(*) n from contacts where vip = 1").Rows[0][0]);

        db.AlterTable("contacts", "Clients", "clients", [new("phone", "text")], [new("vip", "important")], []);
        var clients = db.Table("clients");
        Assert.Equal(["id", "name", "email", "important", "created_at", "updated_at", "phone"], clients.Columns.Select(c => c.Name));
        Assert.Equal("Clients", clients.Description);
        Assert.Equal("clients", db.Table("deals").Columns.Single(c => c.Name == "contact_id").References);
        Assert.Throws<DataException>(() => db.AlterTable("clients", null, null, [new("must", "text", Required: true)], [], []));

        Assert.Equal(1, db.Delete("deals", Row(new { contact_id = new[] { 1, 2 } })));
        db.DropTable("deals");
        Assert.Equal(["clients"], db.Tables().Select(t => t.Name));
        Assert.Contains("- clients (2 rows): Clients", db.Prompt());
    }

    [Theory]
    [InlineData("DELETE FROM contacts")]
    [InlineData("SELECT 1; DROP TABLE contacts")]
    [InlineData("ATTACH DATABASE 'x.db' AS x")]
    [InlineData("SELECT * FROM pragma_table_info('x') -- pragma")]
    [InlineData("SELECT 'unterminated")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x; PRAGMA foreign_keys = OFF")]
    public void Database_queries_are_one_read_only_select(string sql)
    {
        if (sql.Contains("pragma_table_info")) Assert.Equal(sql, Database.ReadOnlySql(sql)); // pragma_ functions read, the keyword in a comment is ignored
        else Assert.Throws<DataException>(() => Database.ReadOnlySql(sql));
    }

    [Fact]
    public void Database_stops_a_runaway_query()
    {
        var db = new Database(new Workspace(root));
        var e = Assert.Throws<DataException>(() => db.Query("WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n) SELECT count(*) FROM n"));
        Assert.Contains("longer than", e.Message);
    }

    [Fact]
    public async Task Database_tools_answer_refusals_the_model_can_fix()
    {
        var tools = new Database(new Workspace(root)).Tools().OfType<AIFunction>().ToDictionary(t => t.Name);
        var refused = (await tools["db_insert"].InvokeAsync(new AIFunctionArguments { ["table"] = "nope", ["rows"] = new[] { new Dictionary<string, object>() } }))?.ToString();
        Assert.Contains("There is no table nope", refused);
        var schema = (await tools["db_schema"].InvokeAsync(new AIFunctionArguments()))?.ToString();
        Assert.Contains("\"success\":true", schema);
    }

    [Fact]
    public void Schedules_run_when_due_once_and_repeat_by_cron()
    {
        var schedules = new Schedules(new Workspace(root), TimeZoneInfo.Utc);
        var now = new DateTimeOffset(2026, 11, 2, 6, 0, 0, TimeSpan.Zero); // a Monday
        var daily = schedules.Create("Morning", "Sum up yesterday's notes.", "0 7 * * 1-5", null, now);
        var once = schedules.Create("Call", "Remind me to call ACME.", null, now.AddHours(3), now);
        Assert.Equal(now.AddHours(1), daily.NextRunAt);
        Assert.Throws<ArgumentException>(() => schedules.Create("x", "y", null, null, now));
        Assert.Throws<ArgumentException>(() => schedules.Create("x", "y", "not cron", null, now));
        Assert.Throws<ArgumentException>(() => schedules.Create("x", "y", null, now.AddHours(-1), now));

        Assert.Empty(schedules.TakeDue(now));
        Assert.Equal([daily.Id], schedules.TakeDue(now.AddHours(1)).Select(s => s.Id));
        Assert.Empty(schedules.TakeDue(now.AddHours(1))); // taken: never twice
        Assert.Equal(now.AddDays(1).AddHours(1), schedules.List().Single(s => s.Id == daily.Id).NextRunAt);

        // Down for days: the missed runs come once.
        var later = now.AddDays(4).AddHours(4);
        Assert.Equal(2, schedules.TakeDue(later).Count);
        Assert.False(schedules.List().Single(s => s.Id == once.Id).Enabled); // a one-off is done
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 7, 0, 0, TimeSpan.Zero), schedules.List().Single(s => s.Id == daily.Id).NextRunAt); // skips the weekend

        Assert.Contains("Morning: next 2026-11-09 07:00 (cron 0 7 * * 1-5)", schedules.ListSchedules());
        Assert.Equal("Cancelled.", schedules.CancelSchedule(once.Id.ToString()));
        Assert.Equal("No such task.", schedules.CancelSchedule(once.Id.ToString()));
        Assert.StartsWith("Error: run_at", schedules.ScheduleTask("x", "y", run_at: "tomorrow"));
    }

    [Fact]
    public async Task Agent_runs_a_due_task_as_a_new_thread()
    {
        var model = new ScriptedModel(Says("Three notes from yesterday."));
        var agent = new Agent(new Workspace(root), model, new LetterEmbeddings(), Key);
        var task = agent.Schedules.Create("Morning", "Sum up yesterday's notes.", null, DateTimeOffset.UtcNow.AddMinutes(1));

        await agent.RunDueSchedulesAsync(DateTimeOffset.UtcNow.AddMinutes(2));

        var done = agent.Schedules.List().Single();
        Assert.Equal("done", done.LastStatus);
        Assert.Equal("Three notes from yesterday.", agent.Threads.Load(done.LastThread!.Value).Last().Text);
        Assert.Equal("Sum up yesterday's notes.", Assert.Single(agent.Threads.List()).Title);
        Assert.Contains(model.LastOptions!.Tools!, t => t.Name == "schedule_task");
    }

    /// <summary>Embeds a text as its letter counts: similar wording, similar vectors.</summary>
    internal sealed class LetterEmbeddings(string model = "letters") : IEmbeddingGenerator<string, Embedding<float>>
    {
        public int Texts { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken ct = default)
        {
            var result = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var text in values)
            {
                Texts++;
                var v = new float[26];
                foreach (var ch in text.ToLowerInvariant()) if (ch is >= 'a' and <= 'z') v[ch - 'a']++;
                result.Add(new Embedding<float>(v));
            }
            return Task.FromResult(result);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(EmbeddingGeneratorMetadata) ? new EmbeddingGeneratorMetadata("letters", defaultModelId: model) : null;
        public void Dispose() { }
    }

    /// <summary>A model that answers each call with the next scripted response.</summary>
    internal sealed class ScriptedModel(params ChatResponse[] responses) : IChatClient
    {
        readonly Queue<ChatResponse> queue = new(responses);

        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            LastOptions = options ?? LastOptions; // side calls (rule checks) carry none
            return Task.FromResult(queue.TryDequeue(out var r) ? r : throw new InvalidOperationException("Model is down."));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var update in (await GetResponseAsync(messages, options, ct)).ToChatResponseUpdates()) yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
