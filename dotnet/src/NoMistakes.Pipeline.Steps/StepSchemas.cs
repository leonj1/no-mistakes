namespace NoMistakes.Pipeline.Steps;

/// <summary>
/// JSON schemas for structured agent output, ported verbatim from Go's
/// internal/pipeline/steps common.go and common_fix.go. Passed to the agent so
/// it returns findings/summary in the expected shape.
/// </summary>
internal static class StepSchemas
{
    public const string CommitSummary = """
{
	"type": "object",
	"properties": {
		"summary": {"type": "string"}
	},
	"required": ["summary"]
}
""";

    public const string Findings = """
{
	"type": "object",
	"properties": {
		"findings": {
			"type": "array",
			"items": {
				"type": "object",
				"properties": {
					"id": {"type": "string"},
					"severity": {"type": "string", "enum": ["error", "warning", "info"]},
					"file": {"type": "string"},
					"line": {"type": "integer"},
					"description": {"type": "string"},
					"action": {"type": "string", "enum": ["no-op", "auto-fix", "ask-user"]}
				},
				"required": ["severity", "description", "action"]
			}
		},
		"summary": {"type": "string"},
		"tested": {"type": "array", "items": {"type": "string"}},
		"testing_summary": {"type": "string"}
	},
	"required": ["findings", "summary"]
}
""";

    public const string TestFindings = """
{
	"type": "object",
	"properties": {
		"findings": {
			"type": "array",
			"items": {
				"type": "object",
				"properties": {
					"id": {"type": "string"},
					"severity": {"type": "string", "enum": ["error", "warning", "info"]},
					"file": {"type": "string"},
					"line": {"type": "integer"},
					"description": {"type": "string"},
					"action": {"type": "string", "enum": ["no-op", "auto-fix", "ask-user"]}
				},
				"required": ["severity", "description", "action"]
			}
		},
		"summary": {"type": "string"},
		"tested": {"type": "array", "items": {"type": "string"}},
		"testing_summary": {"type": "string"},
		"artifacts": {
			"type": "array",
			"items": {
				"type": "object",
				"properties": {
					"kind": {"type": "string"},
					"label": {"type": "string"},
					"path": {"type": "string"},
					"url": {"type": "string"},
					"content": {"type": "string"}
				},
				"required": ["label"]
			}
		}
	},
	"required": ["findings", "summary", "tested", "testing_summary", "artifacts"]
}
""";

    public const string ReviewFindings = """
{
	"type": "object",
	"properties": {
		"findings": {
			"type": "array",
			"items": {
				"type": "object",
				"properties": {
					"id": {"type": "string"},
					"severity": {"type": "string", "enum": ["error", "warning", "info"]},
					"file": {"type": "string"},
					"line": {"type": "integer"},
					"description": {"type": "string"},
					"action": {"type": "string", "enum": ["no-op", "auto-fix", "ask-user"]}
				},
				"required": ["severity", "description", "action"]
			}
		},
		"risk_level": {"type": "string", "enum": ["low", "medium", "high"]},
		"risk_rationale": {"type": "string"}
	},
	"required": ["findings", "risk_level", "risk_rationale"]
}
""";
}
