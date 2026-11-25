using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NeoIDE.Services;

/// <summary>Where an abbreviation is expanded.</summary>
public static class TemplateContext
{
    public const string Twig = "Twig";               // Twig templates (outside {{ }} / {% %})
    public const string Php = "PHP";                 // PHP code
    public const string PhpView = "PHP view";        // the HTML of a .php file (NeoPHP views)
    public const string Html = "HTML";               // HTML files, and the HTML of Twig / PHP views
    public static readonly string[] All = { Twig, Php, PhpView, Html };
}

/// <summary>
/// A live template: <see cref="Abbreviation"/> + Tab inserts <see cref="Body"/>. In the body, <c>$name$</c> is a field
/// (Tab goes to the next one, the same name is typed once for all its places), <c>$name:default$</c> a field with
/// its default text, <c>$END$</c> the caret at the end, <c>$SELECTION$</c> the selected text and <c>$$</c> a "$".
/// A tab in the body is one indentation level; the lines keep the indentation of the abbreviation.
/// </summary>
public sealed class LiveTemplate
{
    public string Abbreviation { get; set; } = "";
    public string Context { get; set; } = TemplateContext.Twig;
    public string Description { get; set; } = "";
    public string Body { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public LiveTemplate Clone() => (LiveTemplate)MemberwiseClone();
}

/// <summary>One piece of a parsed template body.</summary>
public abstract record TemplatePart;
public sealed record TemplateText(string Text) : TemplatePart;
public sealed record TemplateField(string Name, string Default) : TemplatePart;
public sealed record TemplateEnd : TemplatePart;
public sealed record TemplateSelection : TemplatePart;

/// <summary>The templates of the user (templates.json in the settings folder), the defaults when there is none.</summary>
public static class LiveTemplates
{
    private static List<LiveTemplate>? _all;
    public static event Action? Changed;

    private static string FilePath => Path.Combine(AppSettings.Directory, "templates.json");

    public static IReadOnlyList<LiveTemplate> All
    {
        get
        {
            if (_all != null) return _all;
            try
            {
                if (File.Exists(FilePath))
                    _all = JsonSerializer.Deserialize<List<LiveTemplate>>(File.ReadAllText(FilePath));
            }
            catch { _all = null; }
            _all ??= Defaults();
            // defaults added by a later version of NeoIDE (not removed by the user: they are kept disabled when removed)
            foreach (var d in Defaults().Where(d => !_all.Any(t => t.Abbreviation == d.Abbreviation && t.Context == d.Context)))
                if (!RemovedDefaults.Contains(Key(d))) _all.Add(d);
            return _all;
        }
    }

    private static HashSet<string> RemovedDefaults => new(AppSettings.Current.RemovedTemplates ?? new List<string>());

    private static string Key(LiveTemplate t) => t.Context + "|" + t.Abbreviation;

    public static void Save(IEnumerable<LiveTemplate> templates)
    {
        _all = templates.Select(t => t.Clone()).ToList();
        // a default the user removed must not come back with the next start
        AppSettings.Current.RemovedTemplates = Defaults().Select(Key).Where(k => !_all.Any(t => Key(t) == k)).ToList();
        AppSettings.Current.Save();
        Directory.CreateDirectory(AppSettings.Directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(_all, new JsonSerializerOptions { WriteIndented = true }));
        Changed?.Invoke();
    }

    /// <summary>Test harness: forget the cached list.</summary>
    public static void Reload() => _all = null;

    /// <summary>The enabled template of this abbreviation in one of the contexts (the first context wins).</summary>
    public static LiveTemplate? Find(string abbreviation, IEnumerable<string> contexts)
    {
        foreach (var c in contexts)
            if (All.FirstOrDefault(t => t.Enabled && t.Context == c && t.Abbreviation == abbreviation) is { } t) return t;
        return null;
    }

    public static IEnumerable<LiveTemplate> For(IEnumerable<string> contexts) => All.Where(t => t.Enabled && contexts.Contains(t.Context));

    // ------------------------------------------------------------------ body

    private static readonly Regex Variable = new(@"\$\$|\$(?<name>[A-Za-z_][\w]*)(?::(?<default>[^$\r\n]*))?\$", RegexOptions.Compiled);

    /// <summary>The parts of a body, its lines indented with <paramref name="baseIndent"/> and its tabs as <paramref name="unit"/>.</summary>
    public static List<TemplatePart> Parse(string body, string baseIndent = "", string unit = "    ", string newLine = "\n")
    {
        body = body.Replace("\r\n", "\n").Replace("\t", unit).Replace("\n", newLine + baseIndent);
        var parts = new List<TemplatePart>();
        var text = new StringBuilder();
        int last = 0;
        foreach (Match m in Variable.Matches(body))
        {
            text.Append(body, last, m.Index - last);
            last = m.Index + m.Length;
            if (m.Value == "$$") { text.Append('$'); continue; }
            var name = m.Groups["name"].Value;
            if (text.Length > 0) { parts.Add(new TemplateText(text.ToString())); text.Clear(); }
            parts.Add(name switch
            {
                "END" => new TemplateEnd(),
                "SELECTION" => new TemplateSelection(),
                _ => new TemplateField(name, m.Groups["default"].Success ? m.Groups["default"].Value : name),
            });
        }
        text.Append(body, last, body.Length - last);
        if (text.Length > 0) parts.Add(new TemplateText(text.ToString()));
        return parts;
    }

    /// <summary>The body as plain text (fields with their default), for the preview of the settings and the tests.</summary>
    public static string Preview(string body, string unit = "    ") =>
        string.Concat(Parse(body, "", unit).Select(p => p switch
        {
            TemplateText t => t.Text,
            TemplateField f => f.Default,
            _ => "",
        }));

    // ------------------------------------------------------------------ defaults

    public static List<LiveTemplate> Defaults()
    {
        var list = new List<LiveTemplate>();
        void T(string context, string abbr, string description, string body) =>
            list.Add(new LiveTemplate { Context = context, Abbreviation = abbr, Description = description, Body = body });

        // ---- Twig
        var tw = TemplateContext.Twig;
        T(tw, "if", "{% if %} … {% endif %}", "{% if $condition$ %}\n\t$END$\n{% endif %}");
        T(tw, "ife", "{% if %} … {% else %} … {% endif %}", "{% if $condition$ %}\n\t$END$\n{% else %}\n\t\n{% endif %}");
        T(tw, "for", "{% for item in items %}", "{% for $item$ in $items$ %}\n\t$END$\n{% endfor %}");
        T(tw, "fore", "{% for %} … {% else %} (empty)", "{% for $item$ in $items$ %}\n\t$END$\n{% else %}\n\t$empty:Nothing yet.$\n{% endfor %}");
        T(tw, "fork", "{% for key, value in items %}", "{% for $key$, $value$ in $items$ %}\n\t$END$\n{% endfor %}");
        T(tw, "block", "{% block %} … {% endblock %}", "{% block $name$ %}\n\t$END$\n{% endblock %}");
        T(tw, "ext", "{% extends %}", "{% extends '$template:base.html.twig$' %}\n\n{% block $block:body$ %}\n\t$END$\n{% endblock %}");
        T(tw, "inc", "{{ include() }}", "{{ include('$template$') }}$END$");
        T(tw, "incw", "{{ include() }} with variables", "{{ include('$template$', { $name$: $value$ }) }}$END$");
        T(tw, "emb", "{% embed %} … {% endembed %}", "{% embed '$template$' %}\n\t$END$\n{% endembed %}");
        T(tw, "set", "{% set name = value %}", "{% set $name$ = $value$ %}$END$");
        T(tw, "setb", "{% set name %} … {% endset %}", "{% set $name$ %}\n\t$END$\n{% endset %}");
        T(tw, "out", "{{ … }}", "{{ $END$ }}");
        T(tw, "dump", "{{ dump() }}", "{{ dump($var$) }}$END$");
        T(tw, "path", "{{ path('route') }}", "{{ path('$route$') }}$END$");
        T(tw, "url", "{{ url('route') }}", "{{ url('$route$') }}$END$");
        T(tw, "asset", "{{ asset('…') }}", "{{ asset('$path$') }}$END$");
        T(tw, "trans", "{{ '…'|trans }}", "{{ '$key$'|trans }}$END$");
        T(tw, "macro", "{% macro %} … {% endmacro %}", "{% macro $name$($arguments:$) %}\n\t$END$\n{% endmacro %}");
        T(tw, "import", "{% import '…' as … %}", "{% import '$template$' as $alias:macros$ %}$END$");
        T(tw, "apply", "{% apply filter %} … {% endapply %}", "{% apply $filter:upper$ %}\n\t$SELECTION$$END$\n{% endapply %}");
        T(tw, "with", "{% with %} … {% endwith %}", "{% with { $name$: $value$ } %}\n\t$END$\n{% endwith %}");
        T(tw, "csrf", "hidden CSRF token field", "<input type=\"hidden\" name=\"_token\" value=\"{{ csrf_token('$id$') }}\">$END$");
        T(tw, "com", "{# comment #}", "{# $END$ #}");

        // ---- NeoPHP views (HTML of .php files)
        var pv = TemplateContext.PhpView;
        T(pv, "ext", "extend the layout", "<?php $$this->extend('$layout:base$') ?>\n\n<?php $$this->start('$section:content$') ?>\n$END$\n<?php $$this->stop() ?>");
        T(pv, "sec", "start … stop a section", "<?php $$this->start('$name:content$') ?>\n$END$\n<?php $$this->stop() ?>");
        T(pv, "e", "<?= $this->e(…) ?>", "<?= $$this->e($END$) ?>");
        T(pv, "eh", "<?= … ?> (raw)", "<?= $END$ ?>");
        T(pv, "php", "<?php … ?>", "<?php $END$ ?>");
        T(pv, "if", "<?php if (…): ?> … <?php endif ?>", "<?php if ($condition$): ?>\n\t$END$\n<?php endif ?>");
        T(pv, "ife", "if … else … endif", "<?php if ($condition$): ?>\n\t$END$\n<?php else: ?>\n\t\n<?php endif ?>");
        T(pv, "for", "<?php foreach (…): ?> … <?php endforeach ?>", "<?php foreach ($$$items$ as $$$item$): ?>\n\t$END$\n<?php endforeach ?>");
        T(pv, "fork", "foreach with key", "<?php foreach ($$$items$ as $$$key$ => $$$value$): ?>\n\t$END$\n<?php endforeach ?>");
        T(pv, "inc", "include a template", "<?= $$this->include('$template$') ?>$END$");
        T(pv, "sect", "<?= $this->section(…) ?>", "<?= $$this->section('$name:content$') ?>$END$");
        T(pv, "path", "<?= $this->path('route') ?>", "<?= $$this->e($$this->path('$route$')) ?>$END$");
        T(pv, "asset", "<?= $this->asset('…') ?>", "<?= $$this->e($$this->asset('$path$')) ?>$END$");

        // ---- PHP code
        var php = TemplateContext.Php;
        T(php, "fore", "foreach ($items as $item)", "foreach ($$$items$ as $$$item$) {\n\t$END$\n}");
        T(php, "forek", "foreach ($items as $key => $value)", "foreach ($$$items$ as $$$key$ => $$$value$) {\n\t$END$\n}");
        T(php, "fori", "for ($i = 0; …)", "for ($$$i$ = 0; $$$i$ < $count$; $$$i$++) {\n\t$END$\n}");
        T(php, "if", "if (…) { }", "if ($condition$) {\n\t$END$\n}");
        T(php, "ife", "if (…) { } else { }", "if ($condition$) {\n\t$END$\n} else {\n\t\n}");
        T(php, "try", "try { } catch (…) { }", "try {\n\t$SELECTION$$END$\n} catch ($exception:\\Throwable$ $$e) {\n\t\n}");
        T(php, "pubf", "public function", "public function $name$($params:$): $type:void$\n{\n\t$END$\n}");
        T(php, "prof", "protected function", "protected function $name$($params:$): $type:void$\n{\n\t$END$\n}");
        T(php, "prif", "private function", "private function $name$($params:$): $type:void$\n{\n\t$END$\n}");
        T(php, "pubsf", "public static function", "public static function $name$($params:$): $type:void$\n{\n\t$END$\n}");
        T(php, "__c", "constructor", "public function __construct($END$)\n{\n}");
        T(php, "prop", "private property", "private $type:string$ $$$name$;$END$");
        T(php, "const", "private const", "private const $NAME$ = $value$;$END$");
        T(php, "rt", "return …;", "return $END$;");
        T(php, "thr", "throw new …", "throw new $exception:\\RuntimeException$('$message$');$END$");
        T(php, "dd", "dd(…)", "dd($END$);");
        T(php, "dump", "dump(…)", "dump($END$);");
        T(php, "route", "#[Route(…)]", "#[Route('$path:/$', name: '$name$', methods: ['$method:GET$'])]$END$");
        T(php, "action", "NeoPHP controller action", "#[Route('$path:/$', name: '$name$', methods: ['GET'])]\npublic function $action:index$(): Response\n{\n\treturn $$this->render('$template$', [\n\t\t$END$\n\t]);\n}");
        T(php, "render", "return $this->render(…)", "return $$this->render('$template$', [\n\t$END$\n]);");
        T(php, "redirect", "return $this->redirectToRoute(…)", "return $$this->redirectToRoute('$route$');$END$");
        T(php, "json", "return $this->json(…)", "return $$this->json([\n\t$END$\n]);");
        return list;
    }
}
