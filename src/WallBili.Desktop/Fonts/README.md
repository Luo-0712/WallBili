# UI font assets

This software distributes **MiSans Latin**, provided by Xiaomi, and **Noto Sans SC**.
Neither interface font needs to be installed into Windows. Keep the `Fonts/`
directory when copying or publishing the application.

The three unmodified static OTF files come from Xiaomi's official package:

- Package: https://hyperos.mi.com/font-download/MiSans_Latin.zip
- Download page: https://hyperos.mi.com/font/en/download/
- License: `MiSans-License.pdf`, copied unchanged from Xiaomi's official site.

`NotoSansSC-VF.ttf` is an unchanged copy of the Noto Sans SC font already on the
development machine. Its SIL Open Font License is in `NotoSansSC-LICENSE.txt`;
the upstream project is https://github.com/notofonts/noto-cjk .

Regular, Demibold and Semibold Latin faces are private application fonts.

Original font weights are 330, 450 and 520 respectively. Do not rewrite their
metadata or synthesize extra faces. The Chinese heading role uses the distributed
font's real Medium (500) face, rather than requesting an absent 600 face.
