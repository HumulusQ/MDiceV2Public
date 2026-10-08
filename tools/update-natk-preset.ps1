# Update NATK Preset in BuiltinPresets.cpp
$filePath = "C:\Users\Humulus.MSI\Documents\Mydata\Programming\MDiceV2\ABot\ABot.Core\src\BuiltinPresets.cpp"

# Read the file with UTF8 encoding
$content = [System.IO.File]::ReadAllText($filePath, [System.Text.Encoding]::UTF8)

# Define the old NATK implementation to replace
$oldNATK = @'
    // 娉ㄥ唽 NATK (Normal Attack) ANKE 棰勮
    // NATK 鏄竴涓姞鏉冮殢鏈洪璁撅紝灞曠ず濡備綍閫氳繃 ANKE 瀹炵幇姒傜巼鎬ф敾鍑?
    auto natk_anke = std::make_unique<AnkePreset>("natk");
    
    // 娣诲姞鏅€氭敾鍑婚€夐」锛堟潈閲?0锛?
    AnkeOption normal_hit;
    normal_hit.name = "normal_hit";
    normal_hit.weight = 80;
    normal_hit.script = nullptr;  // 浣跨敤榛樿浼ゅ璁＄畻
    natk_anke->AddOption(std::move(normal_hit));
    
    // 娣诲姞鏆村嚮閫夐」锛堟潈閲?5锛?
    AnkeOption critical_hit;
    critical_hit.name = "critical_hit";
    critical_hit.weight = 15;
    critical_hit.script = nullptr;  // TODO: 缂栬瘧 crit 鑴氭湰
    natk_anke->AddOption(std::move(critical_hit));
    
    // 娣诲姞闂伩閫夐」锛堟潈閲?锛?
    AnkeOption miss;
    miss.name = "miss";
    miss.weight = 5;
    miss.script = nullptr;
    natk_anke->AddOption(std::move(miss));
    
    natk_anke->SetBuiltin(true);
    registry->RegisterAnke("natk", std::move(natk_anke));
    
    // 娉ㄦ剰: 涓嶉缃郴缁熸妧鑳芥垨绯荤粺鐘舵€?
    // 鎵€鏈夌殑鎶€鑳藉拰鐘舵€侀兘搴旇鐢辩敤鎴峰湪鍏惰鑹插崱涓畾涔?
    // 閫氳繃 SkillDef 鍜?StateDefinition 鐏垫椿鍦颁紶鍏ュ弬鏁?
'@

# Define the new NATK implementation
$newNATK = @'
    // ========== 娉ㄥ唽 NATK (Normal Attack) ANKE 棰勮 ==========
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
'@

# Replace the old implementation with the new one
$newContent = $content -replace [regex]::Escape($oldNATK), $newNATK

# Write the file back with UTF8 encoding
[System.IO.File]::WriteAllText($filePath, $newContent, [System.Text.Encoding]::UTF8)

Write-Host "NATK Preset has been updated successfully!"
