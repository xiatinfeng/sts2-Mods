using System.Collections.Generic;
using Godot;

namespace Sts2SpawnCheat.Core;

/// <summary>
/// 简易 i18n 支持 —— 检测游戏语言，自动切换中英文。
/// </summary>
public static class I18n
{
    private static string _lang = "zh";
    private static bool _inited;

    private static void EnsureInit()
    {
        if (_inited) return;
        _inited = true;
        try
        {
            var loc = TranslationServer.GetLocale();
            _lang = loc.StartsWith("zh") ? "zh" : "en";
        }
        catch { _lang = "zh"; }
    }

    public static string T(string key)
    {
        EnsureInit();
        return _lang == "en" && _en.ContainsKey(key) ? _en[key] : (_zh.ContainsKey(key) ? _zh[key] : key);
    }

    private static readonly Dictionary<string, string> _zh = new()
    {
        // 主 Tab
        ["tab_cards"] = "卡牌",
        ["tab_relics"] = "遗物",
        ["tab_potions"] = "药水",
        ["tab_resources"] = "资源",
            ["tab_queue"] = "队列",
        ["tab_favorites"] = "收藏",

        // 特殊筛选
        ["filter_all"] = "全部",
        ["filter_colorless"] = "无色卡",
        ["filter_status"] = "状态卡",

        // 卡牌稀有度
        ["rarity_basic"] = "基本",
        ["rarity_common"] = "普通",
        ["rarity_uncommon"] = "罕见",
        ["rarity_rare"] = "稀有",
        ["rarity_ancient"] = "远古",
        ["rarity_curse"] = "诅咒",

        // 遗物稀有度
        ["rarity_relic_event"] = "事件",

        // 药水稀有度
        ["rarity_potion_event"] = "事件",
        ["rarity_token"] = "代币",

        // 搜索
        ["search"] = "搜索:",
        ["search_card"] = "搜索卡名...",
        ["search_relic"] = "遗物 ID/名称...",
        ["search_potion"] = "药水 ID/名称...",

        // 额外卡设置
        ["extra"] = "额外",
        ["unit_cards"] = "张",

        // 注入模式
        ["inject_add"] = "额外添加",
        ["inject_replace"] = "替换原卡",

        // 操作按钮
        ["mark"] = "标记",
        ["marked"] = "\u2713 已标记",
        ["mark_short"] = "\u2713",
        ["clear"] = "\U0001f5d1 清空",
        ["inject"] = "\u2726 注入下一战",

        // 收藏
        ["clear_all"] = "全部清空",
        ["clear_cards"] = "清空卡牌",
        ["clear_relics"] = "清空遗物",
        ["clear_potions"] = "清空药水",
        ["fav_empty"] = "暂无收藏 — 在卡牌/遗物/药水 Tab 中点击 \u2606 收藏",
        ["fav_total"] = "共 {0} 件收藏",

        // 状态栏
        ["status_bar"] = "{0} 关闭 | 标记卡牌后会在下一次卡牌奖励中注入",

        // 队列 & 附魔
        ["queue_reuse"] = "刷新不消耗队列",
        ["enchant_header"] = "⚡ 附魔/升级",
        ["upgrade"] = "升级",
        ["delete"] = "删除",
        ["save_current"] = "保存当前",
        ["append"] = "追加",
        ["column_cards"] = "卡牌",
        ["column_relics"] = "遗物",
        ["column_potions"] = "药水",
        ["no_enchant"] = "无附魔",

        // 通用
        ["empty_list"] = "(空)",
        ["clear_confirm"] = "确认清空所有 {0} 张已标记卡牌？",
        ["clear_title"] = "清空确认",
        ["clear_btn"] = "清空",
        ["marked_count"] = "已标记 {0}/{1} 张",
        ["marked_inject_off"] = "已标记 {0} 张（注入关闭）",
        ["queue_label"] = "替换队列: [{0}]",
        ["count_items"] = "{0} 件",
        ["count_types"] = "{0} 种",
        ["cost_type"] = "费用: {0}  |  {1}",

        // 资源 Tab
        ["gold_section"] = "─ 金币 ─────────────────────",
        ["add_gold"] = "+ 金币",
        ["set_gold"] = "= 设置",
        ["heal"] = "HP 恢复",
        ["full_heal"] = "满血",
        ["energy_section"] = "─ 能量 ─────────────────────",
        ["add_energy"] = "能量+",
        ["shop_section"] = "─ 商店 ─────────────────────",

        // 收藏集
        ["collection_name_hint"] = "收藏集名称...",
        ["append_mode"] = "追加模式（标记 → 收藏集）",
        ["fav_header_cards"] = "卡牌（{0}）",
        ["fav_header_relics"] = "遗物（{0}）",
        ["fav_header_potions"] = "药水（{0}）",
        ["collection_empty"] = "暂无收藏集 — 输入名称并保存当前队列",
    };

    private static readonly Dictionary<string, string> _en = new()
    {
        ["tab_cards"] = "Cards",
        ["tab_relics"] = "Relics",
        ["tab_potions"] = "Potions",
        ["tab_resources"] = "Resources",
            ["tab_queue"] = "Queue",
        ["tab_favorites"] = "Favorites",

        ["filter_all"] = "All",
        ["filter_colorless"] = "Colorless",
        ["filter_status"] = "Status",

        ["rarity_basic"] = "Basic",
        ["rarity_common"] = "Common",
        ["rarity_uncommon"] = "Uncommon",
        ["rarity_rare"] = "Rare",
        ["rarity_ancient"] = "Ancient",
        ["rarity_curse"] = "Curse",

        ["rarity_relic_event"] = "Event",

        ["rarity_potion_event"] = "Event",
        ["rarity_token"] = "Token",

        ["search"] = "Search:",
        ["search_card"] = "Search card name...",
        ["search_relic"] = "Relic ID/name...",
        ["search_potion"] = "Potion ID/name...",

        ["extra"] = "Extra",
        ["unit_cards"] = "",

        ["inject_add"] = "Add extra",
        ["inject_replace"] = "Replace",

        ["mark"] = "Mark",
        ["marked"] = "\u2713 Marked",
        ["mark_short"] = "\u2713",
        ["clear"] = "\U0001f5d1 Clear",
        ["inject"] = "\u2726 Inject",

        ["clear_all"] = "Clear All",
        ["clear_cards"] = "Clear Cards",
        ["clear_relics"] = "Clear Relics",
        ["clear_potions"] = "Clear Potions",
        ["fav_empty"] = "No favorites — click \u2606 in Cards/Relics/Potions tab",
        ["fav_total"] = "Total {0} favorites",

        ["status_bar"] = "F5 Close | Marked cards will be injected in next reward",

        // Queue & Enchant
        ["queue_reuse"] = "Refresh without consuming queue",
        ["enchant_header"] = "⚡ Enchant/Upgrade",
        ["upgrade"] = "Upgrade",
        ["delete"] = "Delete",
        ["save_current"] = "Save Current",
        ["append"] = "Append",
        ["column_cards"] = "Cards",
        ["column_relics"] = "Relics",
        ["column_potions"] = "Potions",
        ["no_enchant"] = "No Enchant",

        // General
        ["empty_list"] = "(Empty)",
        ["clear_confirm"] = "Confirm clear {0} marked cards?",
        ["clear_title"] = "Clear Confirmation",
        ["clear_btn"] = "Clear",
        ["marked_count"] = "Marked {0}/{1}",
        ["marked_inject_off"] = "Marked {0} (inject off)",
        ["queue_label"] = "Queue: [{0}]",
        ["count_items"] = "{0} items",
        ["count_types"] = "{0} types",
        ["cost_type"] = "Cost: {0}  |  {1}",

        // Resources
        ["gold_section"] = "─ Gold ─────────────────────",
        ["add_gold"] = "+ Gold",
        ["set_gold"] = "= Set",
        ["heal"] = "HP Heal",
        ["full_heal"] = "Full Heal",
        ["energy_section"] = "─ Energy ─────────────────────",
        ["add_energy"] = "Energy+",
        ["shop_section"] = "─ Shop ─────────────────────",

        // Collections
        ["collection_name_hint"] = "Collection name...",
        ["append_mode"] = "Append mode (marks → collection)",
        ["fav_header_cards"] = "Cards ({0})",
        ["fav_header_relics"] = "Relics ({0})",
        ["fav_header_potions"] = "Potions ({0})",
        ["collection_empty"] = "No collections — enter name and save current queue",
    };
}
