#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import sys

# Read the file
with open(r'c:\Users\Humulus.MSI\Documents\Mydata\Programming\MDiceV2\ABot\ABot.Core\src\BuiltinPresets.cpp', 'r', encoding='utf-8') as f:
    content = f.read()

# Find and replace the NATK section
old_section_start = '    // 娉ㄥ唽 NATK (Normal Attack) ANKE 棰勮'
old_section_end = '    // 娉ㄦ剰: 涓嶉缃郴缁熸妧鑳芥垨绯荤粺鐘舵€?'

start_idx = content.find(old_section_start)
end_idx = content.find(old_section_end)

if start_idx == -1 or end_idx == -1:
    print("ERROR: Could not find NATK section")
    sys.exit(1)

new_implementation = '''    // ========== 娉ㄥ唽 NATK (Normal Attack) ANKE 棰勮 ==========
    // NATK 鏄竴涓姞鏉冮殢鏈洪璁撅紝灞曠ず濡備綍閫氳繃 ANKE 瀹炵幇姒傜巼鎬ф敾鍑?
    // 鍖呭惈涓変釜鏀诲嚮绫诲瀷: normal_hit, critical_hit, miss
    auto natk_anke = std::make_unique<AnkePreset>("natk");
    
    // ========== 鏅€氭敾鍑婂閫夐」 (80%) ==========
    // 鑴氭湰: 瀵规柊鏁颁造鎴愬熀纭€浼ゅ (20-30)
    {
        AnkeOption normal_hit;
        normal_hit.name = "normal_hit";
        normal_hit.weight = 80;
        
        // 缂栬瘧鑴氭湰: 閫犳垚20-30鐨勫熀纭€浼ゅ
        normal_hit.script = CompileScript(R"(
            // normal_hit 鑴氭湰: 瀵规柊鏁颁造鎴愬熀纭€浼ゅ
            let base_damage = random(20, 30);
            dodamage(base_damage);
        )");
        
        if (normal_hit.script) {
            natk_anke->AddOption(std::move(normal_hit));
        }
    }
    
    // ========== 鏆村嚮閫夐」 (15%) ==========
    // 鑴氭湰: 瀵规柊鏁颁造鎴愭毚鍑诲浼ゅ (40-50, 50% 鏆村嚮鐜?
    {
        AnkeOption critical_hit;
        critical_hit.name = "critical_hit";
        critical_hit.weight = 15;
        
        // 缂栬瘧鑴氭湰: 50% 鑷冲100% 姒傜巼 * 1.5 鍊嶇巼 * 30-40 鍩硅笡浼ゅ
        critical_hit.script = CompileScript(R"(
            // critical_hit 鑴氭湰: 鏆村嚮鏂瑰紡闃叉尃
            let crit_rate = 50;      // 50% 鏆村嚮鐜?
            let crit_mult = 1.5;     // 1.5 鍊嶆暟
            let base_dmg = random(30, 40);
            let is_crit = random(1, 100) <= crit_rate;
            
            if (is_crit) {
                let crit_damage = base_dmg * crit_mult;
                dodamage(crit_damage);
            } else {
                dodamage(base_dmg);
            }
        )");
        
        if (critical_hit.script) {
            natk_anke->AddOption(std::move(critical_hit));
        }
    }
    
    // ========== 闂伩閫夐」 (5%) ==========
    // 鑴氭湰: 涓嶉€犳垚浼ゅ
    {
        AnkeOption miss;
        miss.name = "miss";
        miss.weight = 5;
        
        // 缂栬瘧鑴氭湰: 娌℃湁浼ゅ
        miss.script = CompileScript(R"(
            // miss 鑴氭湰: 涓嶉€犳垚浼ゅ
            // 娌℃湁鎿嶄綔
        )");
        
        if (miss.script) {
            natk_anke->AddOption(std::move(miss));
        }
    }
    
    natk_anke->SetBuiltin(true);
    registry->RegisterAnke("natk", std::move(natk_anke));
    
    '''

# Replace the section
new_content = content[:start_idx] + new_implementation + content[end_idx:]

# Write the file back
with open(r'c:\Users\Humulus.MSI\Documents\Mydata\Programming\MDiceV2\ABot\ABot.Core\src\BuiltinPresets.cpp', 'w', encoding='utf-8') as f:
    f.write(new_content)

print("NATK Preset has been successfully updated!")
