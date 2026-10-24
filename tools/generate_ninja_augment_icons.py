"""Original vector-style ninja augment icons. Run with Python + Pillow.

Renders seven 256px RGBA sprites, preserving existing Unity importer GUIDs.
No downloaded art, fonts or image-generation service required.
"""
from pathlib import Path
import hashlib
import math
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / 'Assets/Images/AugmentIcons'
SCALE = 4


def render(name, theme, symbol):
    ink, accent, bright = theme
    size = 256 * SCALE
    image = Image.new('RGBA', (size, size))
    draw = ImageDraw.Draw(image)

    def polygon(points, color):
        draw.polygon([(int(x*SCALE), int(y*SCALE)) for x,y in points], fill=color)

    def line(points, color, width=3):
        draw.line([(int(x*SCALE), int(y*SCALE)) for x,y in points], fill=color, width=int(width*SCALE), joint='curve')

    def ellipse(box, color, width=2):
        draw.ellipse(tuple(int(v*SCALE) for v in box), outline=color, width=int(width*SCALE))

    for y in range(256):
        factor = .7 + .3 * (1 - abs(y-100)/156)
        color = tuple(int(c*factor) for c in ink) + (255,)
        draw.line((0,y*SCALE,size,y*SCALE), fill=color, width=SCALE)
    mask = Image.new('L', image.size)
    ImageDraw.Draw(mask).rounded_rectangle((4*SCALE,4*SCALE,252*SCALE,252*SCALE), radius=32*SCALE, fill=255)
    image.putalpha(mask)
    draw.rounded_rectangle((8*SCALE,8*SCALE,248*SCALE,248*SCALE), radius=28*SCALE, outline=accent, width=3*SCALE)
    draw.rounded_rectangle((14*SCALE,14*SCALE,242*SCALE,242*SCALE), radius=24*SCALE, outline=(*accent[:3],100), width=SCALE)
    ellipse((34,34,222,222), (*accent[:3],80), 1)
    for x,y in [(25,25),(231,25),(25,231),(231,231)]:
        polygon([(x,y-4),(x+4,y),(x,y+4),(x-4,y)], bright)

    def flame(cx, cy, scale=1):
        pts=[(-35,35),(-45,9),(-32,-22),(-14,-6),(-1,-65),(14,-26),(29,-43),(43,1),(37,29),(14,47),(-14,47)]
        polygon([(cx+x*scale,cy+y*scale) for x,y in pts], accent)
        pts=[(-20,22),(-14,0),(0,-22),(9,2),(19,16),(12,34),(-11,34)]
        polygon([(cx+x*scale,cy+y*scale) for x,y in pts], bright)

    def arrow(start,end,color,width=5):
        line([start,end],color,width)
        dx,dy=end[0]-start[0],end[1]-start[1]
        length=math.hypot(dx,dy); dx/=length; dy/=length
        polygon([end,(end[0]-dx*14-dy*7,end[1]-dy*14+dx*7),(end[0]-dx*14+dy*7,end[1]-dy*14-dx*7)],color)

    def bolt(cx,cy,scale=1):
        pts=[(3,-60),(-31,5),(-3,5),(-20,60),(36,-17),(7,-17),(24,-60)]
        polygon([(cx+x*scale,cy+y*scale) for x,y in pts],bright)

    if symbol == 'heat':
        flame(128,137,1.15)
        for x in [66,190]:
            line([(x,156),(x-6,134),(x+4,111),(x,91)],accent,4)
        polygon([(128,43),(134,55),(128,65),(122,55)],bright)
    elif symbol == 'spread':
        flame(128,145,.86)
        for a,b in [((85,96),(53,63)),((171,96),(203,63)),((78,160),(43,173)),((178,160),(213,173))]:
            arrow(a,b,bright)
    elif symbol == 'blade':
        polygon([(69,177),(128,85),(206,47),(165,125),(86,191)],accent)
        polygon([(82,174),(136,95),(206,47),(157,124)],bright)
        line([(67,166),(100,195)],bright,7)
        line([(80,183),(58,207)],accent,12)
        for i in range(3): line([(44,104+i*19),(99-i*10,104+i*19)],accent,4)
    elif symbol == 'afterimage':
        for x,alpha in [(65,85),(105,150),(147,255)]:
            color=(*accent[:3],alpha)
            polygon([(x,63),(x+28,91),(x+13,121),(x+33,151),(x+9,191),(x-5,183),(x+10,151),(x-15,122),(x,94),(x-12,80)],color)
        arrow((147,206),(204,151),bright,5)
        line([(44,119),(80,119)],bright,3)
        line([(43,145),(72,145)],bright,3)
    elif symbol == 'charges':
        for x in [61,119]:
            polygon([(x,75),(x+35,75),(x+78,127),(x+35,180),(x,180),(x+44,127)],accent)
            line([(x+14,84),(x+50,127),(x+14,170)],bright,4)
        for x in [99,155]:
            draw.rounded_rectangle(tuple(int(v*SCALE) for v in (x-13,199,x+13,211)),radius=4*SCALE,fill=bright)
    elif symbol == 'corridor':
        polygon([(57,188),(119,61),(198,61),(137,188)],(*accent[:3],90))
        line([(57,188),(119,61)],accent,5)
        line([(137,188),(198,61)],accent,5)
        bolt(133,126,.73)
        arrow((64,108),(42,108),bright,3)
        arrow((192,163),(214,163),bright,3)
    elif symbol == 'burst':
        ellipse((54,145,204,204),accent,4)
        ellipse((82,159,179,190),bright,3)
        bolt(132,110,1)
        for angle in [15,50,140,170,215,320]:
            r=math.radians(angle)
            line([(130+58*math.cos(r),168+32*math.sin(r)),(130+83*math.cos(r),168+53*math.sin(r))],accent,4)
    image.resize((256,256),Image.Resampling.LANCZOS).save(OUTPUT/(name+'.png'))


def main():
    fire=((63,24,23),(255,105,49,255),(255,235,163,255))
    dash=((19,37,53),(68,204,207,255),(214,255,240,255))
    storm=((35,25,66),(163,116,255,255),(243,238,255,255))
    icons=[('NinjaHwaDunDamage',fire,'heat'),('NinjaHwaDunRange',fire,'spread'),
           ('NinjaDashDamage',dash,'blade'),('NinjaDashMoveSpeed',dash,'afterimage'),
           ('NinjaDashCharges',dash,'charges'),('NinjaStormRange',storm,'corridor'),
           ('NinjaEndBurst',storm,'burst')]
    template=(OUTPUT/'NinjaHwaDunDamage.png.meta').read_text()
    import re
    for name,theme,symbol in icons:
        render(name,theme,symbol)
        meta=OUTPUT/(name+'.png.meta')
        if not meta.exists():
            guid=hashlib.md5(('PainAndGain/NinjaAugment/'+name).encode()).hexdigest()
            meta.write_text(re.sub(r'^guid: .*$', 'guid: '+guid,template,flags=re.M))
        print(name)


if __name__ == '__main__':
    main()
