using RoundTableMeeting.Domain.Repositories;

namespace RoundTableMeeting.Avatars;

internal static class BuiltinAvatars
{
    public static IReadOnlyList<AvatarEntry> All { get; } = [
        Item("gpt", "GPT"), Item("claude", "Claude"), Item("gemini", "Gemini"), Item("deepseek", "DeepSeek"),
        Item("qwen", "通义千问"), Item("llama", "Llama"), Item("mistral", "Mistral"), Item("grok", "Grok"),
        Item("glm", "GLM"), Item("hunyuan", "混元"),
        Item("codex", "Codex", "tool"), Item("claude-code", "Claude Code", "tool"), Item("opencode", "OpenCode", "tool"),
        Item("workbuddy", "Workbuddy", "tool"), Item("deepseek-harness", "DeepSeek Harness", "tool"), Item("zcode", "ZCode", "tool"),
        Item("fox-mechanic", "小狐机修师", "character"), Item("cloud-sailor", "云朵水手", "character"),
        Item("mushroom-scholar", "蘑菇学者", "character"), Item("panda-boy", "熊猫少年", "character"),
        Item("rabbit-courier", "白兔信使", "character"),
        Item("cat-magician", "黑猫魔术师", "character"),
        Item("frog-raincoat", "雨衣蛙", "character"),
        Item("deer-ranger", "鹿角巡林员", "character"),
        Item("otter-baker", "海獭面包师", "character"),
        Item("hedgehog-inventor", "刺猬发明家", "character"),
        Item("owl-librarian", "雪鸮馆员", "character"),
        Item("sheep-shepherd", "绵羊牧者", "character"),
        Item("tiger-athlete", "白虎运动员", "character"),
        Item("axolotl-diver", "美西螈潜水员", "character"),
        Item("bee-postman", "蜜蜂邮差", "character"),
        Item("sunflower-gardener", "葵花园丁", "character"),
        Item("dragon-knight", "银龙骑士", "character"),
        Item("raccoon-detective", "浣熊侦探", "character"),
        Item("lemon-astronaut", "柠檬宇航员", "character"),
        Item("crane-dancer", "丹顶鹤舞者", "character"),
        Item("ai-girl-gpt", "GPT娘", "ai-girl"),
        Item("ai-girl-claude", "Claude娘", "ai-girl"),
        Item("ai-girl-gemini", "Gemini娘", "ai-girl"),
        Item("ai-girl-qwen", "通义千问娘", "ai-girl"),
        Item("ai-girl-grok", "Grok娘", "ai-girl"),
        Item("ai-girl-glm", "GLM娘", "ai-girl"),
        Item("ai-girl-llama", "Llama娘", "ai-girl"),
        Item("ai-girl-mistral", "Mistral娘", "ai-girl"),
        Item("ai-girl-hunyuan", "混元娘", "ai-girl"),
        Item("ai-girl-deepseek", "大肥鱼", "ai-girl")
    ];

    private static AvatarEntry Item(string id, string name, string category = "model") => new(id, name, $"/avatars/builtin/{id}.png", category);
}
