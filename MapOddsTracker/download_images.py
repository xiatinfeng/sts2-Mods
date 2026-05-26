import urllib.request
import os
import ssl
import time

# Disable SSL verification for simplicity
ssl._create_default_https_context = ssl._create_unverified_context

BASE_URL = "https://sts2.wiki/media"
ASSETS_DIR = os.path.dirname(os.path.abspath(__file__))

bosses = [
    "ceremonial_beast_boss",
    "doormaker_boss",
    "knowledge_demon_boss",
    "lagavulin_matriarch_boss",
    "queen_boss",
    "soul_fysh_boss",
    "test_subject_boss",
    "the_insatiable_boss",
    "vantom_boss",
    "waterfall_giant_boss",
]

monsters = [
    # Special mappings
    "battle_friend_v1",
    "battle_friend_v2",
    "battle_friend_v3",
    "bowlbug_egg",
    "bowlbug_nectar",
    "bowlbug_rock",
    "bowlbug_silk",
    "decimillipede",
    "eye_with_teeth",
    "gas_bomb",
    "orb_head",
    "guardbot",
    "kin_follower",
    "leaf_slime_m",
    "leaf_slime_s",
    "living_smog",
    "noisebot",
    "parafright",
    "assassin_ruby_raider",
    "axe_ruby_raider",
    "brute_ruby_raider",
    "crossbow_ruby_raider",
    "tracker_ruby_raider",
    "stabbot",
    "the_adversary_placeholder",
    "amalgam",
    "tough_egg",
    "twig_slime_m",
    "twig_slime_s",
    "zapbot",
    # Common auto-mapped monsters
    "axebot",
    "sharpshooter",
    "fat_gremlin",
    "mad_gremlin",
    "shield_gremlin",
    "sneaky_gremlin",
    "gremlin_wizard",
    "spike_slime_m",
    "spike_slime_s",
    "acid_slime_m",
    "acid_slime_s",
    "blue_slaver",
    "red_slaver",
    "cultist",
    "jaw_worm",
    "fungi_beast",
    "looter",
    "mugger",
    "centurion",
    "mystic",
    "byrd",
    "chosen",
    "shelled_parasite",
    "spheric_guardian",
    "spire_growth",
    "transient",
    "champ",
    "automaton",
    "awakened_one",
    "time_eater",
    "donu",
    "deca",
    "book_of_stabbing",
    "gremlin_nob",
    "hexaghost",
    "slime_boss",
    "the_guardian",
    "reptomancer",
    "taskmaster",
    "nemesis",
    "giant_head",
    "maw",
    "snecko",
    "crawler",
    "shapes",
    "writhing_mass",
    "darkling",
    "exploder",
    "repulsor",
    "spiker",
    "dagger",
    "torch_head",
    "madman",
    "corrupt_heart",
    "blight",
    "orb_walker",
    "thermic_dynamics",
    "clawbot",
    "laserbot",
    "hammerbot",
    "sentry",
    "bronze_orb",
    "bronze_automaton",
    "the_collector",
    "spire_shield",
    "spire_spear",
    "drowned",
    "rat",
    "writhing_mass",
    "serpent",
    "strange_pod",
    "seedling",
    "mystic_pod",
    "expunger",
    "snake_plant",
    "nemsy",
    "pointy",
    " Romeo ",
    "bear",
    "bronze_orb",
    "centurion",
    "chosen",
    "cultist",
    "dagger",
    "darkling",
    "deca",
    "donu",
    "exploder",
    "fat_gremlin",
    "fungi_beast",
    "giant_head",
    "gremlin_nob",
    "gremlin_wizard",
    "hexaghost",
    "jaw_worm",
    "mad_gremlin",
    "madman",
    "maw",
    "mugger",
    "mystic",
    "nemesis",
    "reptomancer",
    "repulsor",
    "shelled_parasite",
    "shield_gremlin",
    "sneaky_gremlin",
    "snecko",
    "spheric_guardian",
    "spike_slime_m",
    "spike_slime_s",
    "spire_growth",
    "taskmaster",
    "the_guardian",
    "torch_head",
    "transient",
]

def download_file(url, dest):
    try:
        req = urllib.request.Request(url, headers={'User-Agent': 'Mozilla/5.0'})
        with urllib.request.urlopen(req, timeout=15) as response:
            if response.status == 200:
                with open(dest, 'wb') as f:
                    f.write(response.read())
                return True
    except Exception as e:
        print(f"  Failed: {e}")
    return False

print("=== Downloading Boss Images ===")
for boss in bosses:
    url = f"{BASE_URL}/bosses/{boss}.png"
    dest = os.path.join(ASSETS_DIR, "assets", "bosses", f"{boss}.png")
    print(f"Downloading {boss}...", end=" ")
    if download_file(url, dest):
        print("OK")
    else:
        print("FAIL")
    time.sleep(0.3)

print("\n=== Downloading Monster Images ===")
for monster in monsters:
    url = f"{BASE_URL}/monsters/{monster}.png"
    dest = os.path.join(ASSETS_DIR, "assets", "monsters", f"{monster}.png")
    print(f"Downloading {monster}...", end=" ")
    if download_file(url, dest):
        print("OK")
    else:
        print("FAIL")
    time.sleep(0.3)

print("\nDone!")
