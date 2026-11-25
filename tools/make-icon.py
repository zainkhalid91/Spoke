# Draws the Spoke icon: 8 graphite ring segments, one amber, and a needle from the hub.
import math, sys
from PIL import Image, ImageDraw
S=1024; img=Image.new("RGBA",(S,S),(0,0,0,0)); d=ImageDraw.Draw(img)
c=S/2; rO=480; rI=250; n=8; gap=5
for i in range(n):
    a0=-90-22.5+i*45+gap; a1=-90+22.5+i*45-gap
    col=(245,165,36,255) if i==1 else (58,63,74,255)
    d.pieslice([c-rO,c-rO,c+rO,c+rO],a0,a1,fill=col)
d.ellipse([c-rI,c-rI,c+rI,c+rI],fill=(0,0,0,0))
d.ellipse([c-205,c-205,c+205,c+205],fill=(18,20,24,255))
am=math.radians(-90+45); x2=c+235*math.cos(am); y2=c+235*math.sin(am)
d.line([c,c,x2,y2],fill=(245,165,36,255),width=46)
d.ellipse([c-62,c-62,c+62,c+62],fill=(245,165,36,255))
img.save(sys.argv[1],sizes=[(16,16),(20,20),(24,24),(32,32),(40,40),(48,48),(64,64),(256,256)])
img.resize((256,256),Image.LANCZOS).save(sys.argv[2])
