"""Localized Stable Diffusion anime prompt presets and format metadata.

Prompt text is plain English for SD 1.x and uses ordinary comma-separated
descriptions. It intentionally avoids A1111 weighting and bracket syntax.
"""

from __future__ import annotations

from typing import Any


CATALOG_ID = "counterfeit-anime-prompts"
CATALOG_VERSION = 1

FORMAT_DESCRIPTION: dict[str, Any] = {
    "promptLanguage": "en",
    "promptStyle": "Plain English SD 1.x prompt, written as comma-separated descriptive phrases.",
    "syntax": "Plain text only. A1111 weighting, emphasis, and scheduling syntax is unsupported.",
    "contentTokenLimit": "Maximum 75 content tokens. The 77 CLIP positions include BOS and EOS; longer prompts are truncated.",
    "localizedNames": {
        "en": "English display name",
        "zh": "简体中文显示名称",
        "ja": "日本語の表示名",
    },
    "indexing": "Index 0 is the shared negative prompt; positive presets begin at index 1.",
    "embeddingFiles": "embedding-{index}.bytes, one file per catalog entry.",
}


PROMPT_CATALOG: list[dict[str, Any]] = [
    {
        "index": 0,
        "label": "negative_common",
        "category": "negative",
        "name": {"en": "Common negative prompt", "zh": "通用负向提示词", "ja": "共通ネガティブプロンプト"},
        "prompt": "low quality, worst quality, low resolution, blurry, jpeg artifacts, bad anatomy, deformed anatomy, malformed hands, extra fingers, missing fingers, fused fingers, extra limbs, text, watermark, logo, nsfw, nude, nudity, explicit, pornographic, sexual content",
    },
    {
        "index": 1,
        "label": "anime_portrait_adult",
        "category": "character",
        "name": {"en": "Quiet portrait", "zh": "窗边静谧肖像", "ja": "窓辺の静かな肖像"},
        "prompt": "masterpiece, best quality, anime illustration, A calm portrait of an adult anime woman with short dark hair, wearing a blue cardigan, gentle expression, soft window light, simple background.",
    },
    {
        "index": 2,
        "label": "spring_coast",
        "category": "landscape",
        "name": {"en": "Spring coast", "zh": "春日海岸", "ja": "春の海岸"},
        "prompt": "masterpiece, best quality, anime illustration, A peaceful coastal village at sunrise, pastel houses beside a quiet sea, distant mountains, soft clouds, detailed anime landscape.",
    },
    {
        "index": 3,
        "label": "autumn_cafe",
        "category": "city",
        "name": {"en": "Autumn street cafe", "zh": "秋日街角咖啡馆", "ja": "秋の街角カフェ"},
        "prompt": "masterpiece, best quality, anime illustration, A small street cafe in autumn, falling leaves, warm light in the windows, wet pavement, a cozy anime illustration.",
    },
    {
        "index": 4,
        "label": "garden_cat",
        "category": "animal",
        "name": {"en": "Cat by the garden window", "zh": "花园窗边的猫", "ja": "庭を眺める窓辺の猫"},
        "prompt": "masterpiece, best quality, anime illustration, A white cat resting on a windowsill and looking into a green garden in spring, soft morning light, a peaceful illustrated scene.",
    },
    {
        "index": 5,
        "label": "rainy_city_walk",
        "category": "character",
        "name": {"en": "Rainy city walk", "zh": "雨中漫步", "ja": "雨の街を歩く人"},
        "prompt": "masterpiece, best quality, anime illustration, An adult woman under a clear umbrella on a rainy city street, reflective pavement, glowing shop windows, evening light",
    },
    {
        "index": 6,
        "label": "two_friends_seaside",
        "category": "duo",
        "name": {"en": "Friends at the seaside", "zh": "海边的朋友", "ja": "海辺の友人たち"},
        "prompt": "masterpiece, best quality, anime illustration, Two adult friends watching the sunset from a seaside promenade, wind moving their coats, calm waves, warm orange sky",
    },
    {
        "index": 7,
        "label": "travellers_train_station",
        "category": "duo",
        "name": {"en": "Travellers at the station", "zh": "车站里的旅伴", "ja": "駅にいる旅人たち"},
        "prompt": "masterpiece, best quality, anime illustration, Two travellers waiting beside a rural train platform, luggage at their feet, a local train arriving, green hills in the distance",
    },
    {
        "index": 8,
        "label": "mountain_lake",
        "category": "landscape",
        "name": {"en": "Mountain lake morning", "zh": "清晨山间湖泊", "ja": "山の湖の朝"},
        "prompt": "masterpiece, best quality, anime illustration, A clear mountain lake at dawn, pine trees reflected in still water, mist between the peaks, pale blue sky",
    },
    {
        "index": 9,
        "label": "old_city_canal",
        "category": "city",
        "name": {"en": "Old town canal", "zh": "古镇运河", "ja": "古い町の運河"},
        "prompt": "masterpiece, best quality, anime illustration, A quiet old town canal lined with narrow houses, stone bridge, small boats, flower boxes, soft afternoon sunlight",
    },
    {
        "index": 10,
        "label": "corgi_park",
        "category": "animal",
        "name": {"en": "Corgi in the park", "zh": "公园里的柯基", "ja": "公園のコーギー"},
        "prompt": "masterpiece, best quality, anime illustration, A cheerful corgi running through a green park, daisies in the grass, leaves drifting in a light breeze, sunny afternoon",
    },
    {
        "index": 11,
        "label": "bookstore_tabby",
        "category": "animal",
        "name": {"en": "Tabby in a bookshop", "zh": "书店里的虎斑猫", "ja": "本屋のキジトラ猫"},
        "prompt": "masterpiece, best quality, anime illustration, An orange tabby cat curled on a wooden counter in a small bookshop, tall shelves, a sunbeam across the floor",
    },
    {
        "index": 12,
        "label": "cranes_in_wetlands",
        "category": "animal",
        "name": {"en": "Cranes in the wetlands", "zh": "湿地上的丹顶鹤", "ja": "湿地のタンチョウ"},
        "prompt": "masterpiece, best quality, anime illustration, Two red-crowned cranes standing in a quiet marsh, reeds and shallow water, distant mountains, clear winter morning",
    },
    {
        "index": 13,
        "label": "forest_mage",
        "category": "fantasy",
        "name": {"en": "Mage in the forest", "zh": "森林中的魔法师", "ja": "森の魔法使い"},
        "prompt": "masterpiece, best quality, anime illustration, An adult mage carrying a lantern through an ancient forest, long cloak, tiny lights among the trees, blue twilight",
    },
    {
        "index": 14,
        "label": "bamboo_forest_path",
        "category": "landscape",
        "name": {"en": "Path through bamboo", "zh": "竹林小径", "ja": "竹林の小道"},
        "prompt": "masterpiece, best quality, anime illustration, A narrow stone path through a tall bamboo forest, sunbeams through green leaves, small mossy steps, peaceful atmosphere",
    },
    {
        "index": 15,
        "label": "night_city_crossing",
        "category": "city",
        "name": {"en": "Night city crossing", "zh": "夜色中的城市路口", "ja": "夜の街の交差点"},
        "prompt": "masterpiece, best quality, anime illustration, A lively city crossing at night, colorful signs reflected on wet streets, pedestrians beneath umbrellas, tall buildings fading into the sky",
    },
    {
        "index": 16,
        "label": "mecha_hangar",
        "category": "mecha",
        "name": {"en": "Mecha in the hangar", "zh": "机库中的机甲", "ja": "格納庫のメカ"},
        "prompt": "masterpiece, best quality, anime illustration, A blue and white giant robot standing inside a maintenance hangar, overhead work lights, technicians on platforms, detailed mechanical parts",
    },
    {
        "index": 17,
        "label": "robot_city_guardian",
        "category": "science_fiction",
        "name": {"en": "Guardian robot in the city", "zh": "城市守护机器人", "ja": "街を守るロボット"},
        "prompt": "masterpiece, best quality, anime illustration, A small friendly service robot guiding people through a futuristic city plaza, glass towers, green rooftop gardens, bright morning",
    },
    {
        "index": 18,
        "label": "spaceship_pilot",
        "category": "science_fiction",
        "name": {"en": "Pilot above the planet", "zh": "行星上空的飞行员", "ja": "惑星上空のパイロット"},
        "prompt": "masterpiece, best quality, anime illustration, An adult spaceship pilot looking through a cockpit window, distant blue planet, stars across deep space, instrument lights",
    },
    {
        "index": 19,
        "label": "airship_above_clouds",
        "category": "fantasy",
        "name": {"en": "Airship above the clouds", "zh": "云海之上的飞艇", "ja": "雲海の上の飛行船"},
        "prompt": "masterpiece, best quality, anime illustration, A brass airship sailing above a sea of clouds, distant floating islands, golden sunrise, tiny birds in the sky",
    },
    {
        "index": 20,
        "label": "flower_meadow",
        "category": "landscape",
        "name": {"en": "Wildflower meadow", "zh": "野花草甸", "ja": "野花の草原"},
        "prompt": "masterpiece, best quality, anime illustration, A wide meadow filled with blue and yellow wildflowers, distant farmhouse, rolling hills, bright clouds on a summer day",
    },
    {
        "index": 21,
        "label": "snow_fox",
        "category": "animal",
        "name": {"en": "Fox in fresh snow", "zh": "雪地里的狐狸", "ja": "新雪の中のキツネ"},
        "prompt": "masterpiece, best quality, anime illustration, A red fox walking through fresh snow in a quiet forest, delicate footprints, frosted branches, pale winter sunlight",
    },
    {
        "index": 22,
        "label": "shrine_lantern_festival",
        "category": "festival",
        "name": {"en": "Lantern festival at the shrine", "zh": "神社灯笼祭典", "ja": "神社の灯籠祭り"},
        "prompt": "masterpiece, best quality, anime illustration, A traditional shrine path decorated with paper lanterns during a summer festival, families strolling, evening stalls, warm light",
    },
    {
        "index": 23,
        "label": "coastal_lighthouse",
        "category": "landscape",
        "name": {"en": "Lighthouse on the cliffs", "zh": "海崖灯塔", "ja": "海崖の灯台"},
        "prompt": "masterpiece, best quality, anime illustration, A white lighthouse on green coastal cliffs, seabirds circling above the waves, distant fishing boats, clear spring sky",
    },
    {
        "index": 24,
        "label": "snowy_railway_village",
        "category": "city",
        "name": {"en": "Snowy railway village", "zh": "雪中的铁路小镇", "ja": "雪の鉄道の町"},
        "prompt": "masterpiece, best quality, anime illustration, A small railway station in a snowy village, warm light from the waiting room, pine trees, a red train in the distance",
    },
]


def validate_catalog() -> None:
    """Fail early if catalog indices, localization, or prompt syntax drift."""
    expected_indices = list(range(len(PROMPT_CATALOG)))
    indices = [entry.get("index") for entry in PROMPT_CATALOG]
    if indices != expected_indices:
        raise ValueError(f"Catalog indices must be contiguous from zero, got {indices}")
    labels = [entry.get("label") for entry in PROMPT_CATALOG]
    if len(set(labels)) != len(labels) or any(not isinstance(label, str) for label in labels):
        raise ValueError("Catalog labels must be unique strings")
    for entry in PROMPT_CATALOG:
        name = entry.get("name")
        if not isinstance(name, dict) or any(not name.get(locale) for locale in ("en", "zh", "ja")):
            raise ValueError(f"Entry {entry.get('label')} requires en, zh, and ja display names")
        prompt = entry.get("prompt")
        if not isinstance(prompt, str) or not prompt.strip():
            raise ValueError(f"Entry {entry.get('label')} has an empty prompt")
        if any(marker in prompt for marker in ("(", ")", "[", "]")):
            raise ValueError(f"Entry {entry.get('label')} uses unsupported weighted prompt syntax")
        if entry["index"] > 0 and "," not in prompt:
            raise ValueError(f"Positive prompt {entry.get('label')} must use comma-separated text")


validate_catalog()
