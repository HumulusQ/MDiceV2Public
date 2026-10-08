# Update NATK Preset in BuiltinPresets.cpp
$filePath = "c:\Users\Humulus.MSI\Documents\Mydata\Programming\MDiceV2\ABot\ABot.Core\src\BuiltinPresets.cpp"

# Read the file
$lines = @()
$inReplace = $false
$skipCount = 0

$fileContent = Get-Content -Encoding UTF8 $filePath

foreach ($line in $fileContent) {
    # Check if we're at the start of the NATK section
    if ($line -match "// 娉ㄥ唽 NATK" -and -not $inReplace) {
        $inReplace = $true
        # Skip the old implementation
        $lines += "    // ========== 娉ㄥ唽 NATK (Normal Attack) ANKE 棰勮 =========="
        $lines += "    // NATK 鏄竴涓姞鏉冮殢鏈洪璁撅紝灞曠ず濡備綍閫氳繃 ANKE 瀹炵幇姒傜巼鎬ф敾鍑?"
        $lines += "    // 鍖呭惈涓変釜鏀诲嚮绫诲瀷: normal_hit, critical_hit, miss"
        $lines += "    auto natk_anke = std::make_unique<AnkePreset>(""natk"");"
        $lines += "    "
        $lines += "    // ========== 鏅€氭敾鍑婂閫夐」 (80%) =========="
        $lines += "    // 鑴氭湰: 瀵规柊鏁颁造鎴愬熀纭€浼ゅ (20-30)"
        $lines += "    {"
        $lines += "        AnkeOption normal_hit;"
        $lines += "        normal_hit.name = ""normal_hit"";"
        $lines += "        normal_hit.weight = 80;"
        $lines += "        "
        $lines += "        // 缂栬瘧鑴氭湰: 閫犳垚20-30鐨勫熀纭€浼ゅ"
        $lines += "        normal_hit.script = CompileScript(R""("""
        $lines += "            // normal_hit 鑴氭湰: 瀵规柊鏁颁造鎴愬熀纭€浼ゅ"
        $lines += "            let base_damage = random(20, 30);"
        $lines += "            dodamage(base_damage);"
        $lines += "        )"");"
        $lines += "        "
        $lines += "        if (normal_hit.script) {"
        $lines += "            natk_anke->AddOption(std::move(normal_hit));"
        $lines += "        }"
        $lines += "    }"
        $lines += "    "
        $lines += "    // ========== 鏆村嚮閫夐」 (15%) =========="
        $lines += "    // 鑴氭湰: 瀵规柊鏁颁造鎴愭毚鍑诲浼ゅ (40-50, 50% 鏆村嚮鐜?"
        $lines += "    {"
        $lines += "        AnkeOption critical_hit;"
        $lines += "        critical_hit.name = ""critical_hit"";"
        $lines += "        critical_hit.weight = 15;"
        $lines += "        "
        $lines += "        // 缂栬瘧鑴氭湰: 50% 鑷冲100% 姒傜巼 * 1.5 鍊嶇巼 * 30-40 鍩硅笡浼ゅ"
        $lines += "        critical_hit.script = CompileScript(R""("""
        $lines += "            // critical_hit 鑴氭湰: 鏆村嚮鏂瑰紡闃叉尃"
        $lines += "            let crit_rate = 50;      // 50% 鏆村嚮鐜?"
        $lines += "            let crit_mult = 1.5;     // 1.5 鍊嶆暟"
        $lines += "            let base_dmg = random(30, 40);"
        $lines += "            let is_crit = random(1, 100) <= crit_rate;"
        $lines += "            "
        $lines += "            if (is_crit) {"
        $lines += "                let crit_damage = base_dmg * crit_mult;"
        $lines += "                dodamage(crit_damage);"
        $lines += "            } else {"
        $lines += "                dodamage(base_dmg);"
        $lines += "            }"
        $lines += "        )"");"
        $lines += "        "
        $lines += "        if (critical_hit.script) {"
        $lines += "            natk_anke->AddOption(std::move(critical_hit));"
        $lines += "        }"
        $lines += "    }"
        $lines += "    "
        $lines += "    // ========== 闂伩閫夐」 (5%) =========="
        $lines += "    // 鑴氭湰: 涓嶉€犳垚浼ゅ"
        $lines += "    {"
        $lines += "        AnkeOption miss;"
        $lines += "        miss.name = ""miss"";"
        $lines += "        miss.weight = 5;"
        $lines += "        "
        $lines += "        // 缂栬瘧鑴氭湰: 娌℃湁浼ゅ"
        $lines += "        miss.script = CompileScript(R""("""
        $lines += "            // miss 鑴氭湰: 涓嶉€犳垚浼ゅ"
        $lines += "            // 娌℃湁鎿嶄綔"
        $lines += "        )"");"
        $lines += "        "
        $lines += "        if (miss.script) {"
        $lines += "            natk_anke->AddOption(std::move(miss));"
        $lines += "        }"
        $lines += "    }"
        $lines += "    "
        $lines += "    natk_anke->SetBuiltin(true);"
        $lines += "    registry->RegisterAnke(""natk"", std::move(natk_anke));"
        $skipCount = 28  # Skip 28 lines
    }
    elseif ($inReplace -and $skipCount -gt 0) {
        $skipCount--
    }
    elseif ($inReplace -and $skipCount -eq 0 -and $line -match "// 娉ㄦ剰:") {
        $inReplace = $false
        $lines += $line
    }
    elseif (-not $inReplace) {
        $lines += $line
    }
}

# Write back tofile
$lines | Out-File -Encoding UTF8 -FilePath $filePath

Write-Host "NATK Preset update completed."
