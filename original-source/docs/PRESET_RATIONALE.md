# 预设依据

ScreenEase 的默认预设使用独立中文名称，参数来自公开学术论文中关于数字眼疲劳、短波长光、警觉性和夜间屏幕使用的证据。它们属于显示舒适度默认值，不能替代医疗建议、诊断或治疗。

## 使用的论文

- Sheppard AL, Wolffsohn JS. "Digital eye strain: prevalence, measurement and amelioration." BMJ Open Ophthalmology, 2018. DOI: https://doi.org/10.1136/bmjophth-2018-000146
- Rosenfield M. "Computer vision syndrome: a review of ocular causes and potential treatments." Ophthalmic and Physiological Optics, 2011. DOI: https://doi.org/10.1111/j.1475-1313.2011.00834.x
- Brainard GC et al. "Action spectrum for melatonin regulation in humans: evidence for a novel circadian photoreceptor." Journal of Neuroscience, 2001. DOI: https://doi.org/10.1523/JNEUROSCI.21-16-06405.2001
- Cajochen C et al. "High Sensitivity of Human Melatonin, Alertness, Thermoregulation, and Heart Rate to Short Wavelength Light." Journal of Clinical Endocrinology & Metabolism, 2005. DOI: https://doi.org/10.1210/jc.2004-0957
- Chang AM et al. "Evening use of light-emitting eReaders negatively affects sleep, circadian timing, and next-morning alertness." PNAS, 2015. DOI: https://doi.org/10.1073/pnas.1418490112

## 参数映射

| Id | 显示名 | 日间值 | 夜间值 | 依据说明 |
| --- | --- | --- | --- | --- |
| `day-office` | 日间办公 | 6500 K / 100% | 5000 K / 90% | 白天清晰白场，保留任务可见度；夜间回落到更温和的值。 |
| `long-read` | 长读柔光 | 5000 K / 85% | 4200 K / 75% | 长时间文字阅读降低亮度和冷白刺激，减少刺眼感。 |
| `detail-work` | 细节清晰 | 6500 K / 90% | 5000 K / 85% | 接近日光白，适合看细节，同时避免长期满亮度。 |
| `warm-video` | 影音暖光 | 4500 K / 85% | 3700 K / 75% | 放松观看时降低短波长强调，夜间进一步变暖。 |
| `bright-focus` | 高亮专注 | 6500 K / 95% | 5000 K / 85% | 白天高警觉场景保留较高亮度；夜间值收敛。 |
| `low-blue-evening` | 夜间低蓝 | 3700 K / 75% | 3200 K / 65% | 夜间优先方案，依据短波长光对昼夜节律敏感性的研究。 |
| `personal` | 我的方案 | 5000 K / 85% | 4200 K / 75% | 给用户自定义保存的中性起点。 |

## 注意事项

- 色温是软件层的粗略近似。真实的 melanopic 暴露还取决于屏幕光谱、环境光、距离、时间和个体差异。
- 亮度百分比表示 gamma ramp 输出比例；实际屏幕亮度需要亮度计测量。
- 休息提醒来自数字眼疲劳综述中对定期休息、干眼和视觉负荷管理的讨论。
