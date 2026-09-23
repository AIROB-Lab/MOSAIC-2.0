"""Generate labelled documentation schematics and calculated reference plots."""
from pathlib import Path
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.patches import FancyBboxPatch, FancyArrowPatch
import numpy as np
ROOT=Path(__file__).resolve().parents[2]
plt.rcParams.update({"font.family":"DejaVu Sans","svg.fonttype":"none"})
BG,FG,MUTED,CYAN,GOLD,PANEL="#1f1f23","#e1e1e1","#b9c4d2","#42c8ff","#f0bc66","#292b30"
fig=plt.figure(figsize=(210/25.4,86/25.4),facecolor=BG)
ax=fig.add_axes([0,0,1,1],xlim=(0,210),ylim=(0,86));ax.axis("off")
def text(x,y,s,size=8,color=FG,ha="center",weight="normal"):
    ax.text(x,y,s,ha=ha,va="center",fontsize=size,color=color,weight=weight,linespacing=1.5)
def box(x,y,w,h,title,sub):
    ax.add_patch(FancyBboxPatch((x,y),w,h,boxstyle="round,pad=0,rounding_size=2",fc=PANEL,ec=CYAN,lw=.8))
    text(x+w/2,y+h-6,title,9,weight="bold")
    text(x+w/2,y+6,sub,7.2,color=MUTED)
def arrow(x,y,xx,yy):
    ax.add_patch(FancyArrowPatch((x,y),(xx,yy),arrowstyle="-|>",mutation_scale=9,color=GOLD,lw=1))
text(6,79,"Signal processing: follow each output",11,ha="left",weight="bold")
text(6,71,"Clock supplies 256 ticks/s to Signal. One channel; arbitrary amplitude units.",8,ha="left",color=MUTED)
for x,title,sub in [(6,"Signal","8 Hz sine"),(58,"Filtered","32 Hz low-pass"),(110,"Window","256 rows; stride 64")]:box(x,34,42,22,title,sub)
arrow(48,45,58,45);arrow(100,45,110,45)
box(165,49,39,20,"MAV","Mean absolute value")
box(165,20,39,20,"Spectrum","Normalized FFT")
arrow(152,45,165,59);arrow(152,45,165,30)
text(27,27,"Vector [1]",8,color=CYAN)
text(27,21,"256 outputs/s",7.5)
text(79,27,"Vector [1]",8,color=CYAN)
text(79,21,"256 outputs/s",7.5)
text(131,27,"Matrix [256 × 1]",8,color=CYAN)
text(131,21,"4 outputs/s",7.5)
text(184.5,44,"Vector [1] · 4/s",7,color=GOLD)
text(184.5,14,"129 bins × 1 · 4/s",7,color=GOLD)
text(6,6,"Window rows are time samples. MAV values are features. Spectrum rows are frequency bins.",7.6,ha="left",color=MUTED)
fig.savefig(ROOT/"Documentation/images/signal-lab-flow.svg",facecolor=BG)
fig.savefig(ROOT/"output/figures/signal-lab-flow.png",dpi=180,facecolor=BG)
plt.close(fig)

fig,axes=plt.subplots(1,3,figsize=(10.8,3.2),facecolor=BG)
t=np.arange(256)/256
wave=np.sin(2*np.pi*8*t)
mav=np.mean(np.abs(wave))
spectrum=2*np.abs(np.fft.rfft(wave))/256
spectrum[[0,-1]]/=2
data=[(t,wave,"Signal: 8 cycles in 1 second","Signal time (s)","Amplitude"),
      (np.arange(16)/4,np.repeat(mav,16),"MAV: one feature every 250 ms","Feature time (s)","Mean absolute value"),
      (np.arange(33),spectrum[:33],"Spectrum: peak at 8 Hz","Frequency (Hz)","Normalized amplitude")]
for a,(x,y,title,xlabel,ylabel) in zip(axes,data):
    a.set_facecolor(PANEL);a.plot(x,y,color=CYAN,lw=1.5)
    a.set_title(title,color=FG,fontsize=10,pad=12)
    a.set_xlabel(xlabel,color=FG,fontsize=9);a.set_ylabel(ylabel,color=FG,fontsize=9)
    a.tick_params(colors=MUTED,labelsize=8);a.grid(alpha=.12,color=MUTED)
    for spine in a.spines.values():spine.set_color("#617282")
axes[1].scatter(data[1][0],data[1][1],color=GOLD,s=10,zorder=3);axes[1].set_ylim(0,1)
axes[2].set_ylim(0,1.1);axes[2].set_xticks([0,8,16,24,32])
fig.suptitle("Calculated steady-state references • not application screenshots",color=MUTED,fontsize=10,y=.98)
fig.tight_layout(rect=(0,0,1,.90),pad=1.4)
fig.savefig(ROOT/"Documentation/images/signal-lab-reference.svg",facecolor=BG)
fig.savefig(ROOT/"output/figures/signal-lab-reference.png",facecolor=BG,dpi=180)
plt.close(fig)
print(f"Reference MAV: {mav:.6f}")

# SVG keeps editable text; browsers may not have matplotlib's bundled font installed.
for name in ("signal-lab-flow.svg", "signal-lab-reference.svg"):
    path=ROOT/"Documentation/images"/name
    path.write_text(path.read_text(encoding="utf-8").replace("'DejaVu Sans'", "'DejaVu Sans', Arial, sans-serif"),encoding="utf-8")
