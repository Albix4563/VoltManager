/* Lightweight local WebGL visualizer for VoltManager Cooling.
 * No network assets or external rendering libraries are required.
 */
(function () {
    'use strict';

    const instances = new WeakMap();

    function mat4Identity() {
        return [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1];
    }
    function mat4Mul(a, b) {
        const out = new Array(16).fill(0);
        for (let c = 0; c < 4; c++) for (let r = 0; r < 4; r++)
            for (let k = 0; k < 4; k++) out[c * 4 + r] += a[k * 4 + r] * b[c * 4 + k];
        return out;
    }
    function translate(x,y,z) {
        const m = mat4Identity(); m[12]=x; m[13]=y; m[14]=z; return m;
    }
    function scale(x,y,z) {
        const m = mat4Identity(); m[0]=x; m[5]=y; m[10]=z; return m;
    }
    function rotateX(a) {
        const c=Math.cos(a), s=Math.sin(a); return [1,0,0,0, 0,c,s,0, 0,-s,c,0, 0,0,0,1];
    }
    function rotateY(a) {
        const c=Math.cos(a), s=Math.sin(a); return [c,0,-s,0, 0,1,0,0, s,0,c,0, 0,0,0,1];
    }
    function rotateZ(a) {
        const c=Math.cos(a), s=Math.sin(a); return [c,s,0,0, -s,c,0,0, 0,0,1,0, 0,0,0,1];
    }
    function perspective(fov, aspect, near, far) {
        const f=1/Math.tan(fov/2), nf=1/(near-far);
        return [f/aspect,0,0,0, 0,f,0,0, 0,0,(far+near)*nf,-1, 0,0,(2*far*near)*nf,0];
    }
    function compose() {
        let m = mat4Identity();
        for (let i=0;i<arguments.length;i++) m=mat4Mul(m, arguments[i]);
        return m;
    }

    const cubeVertices = new Float32Array([
        -1,-1, 1,  1,-1, 1,  1, 1, 1, -1,-1, 1,  1, 1, 1, -1, 1, 1,
         1,-1,-1, -1,-1,-1, -1, 1,-1,  1,-1,-1, -1, 1,-1,  1, 1,-1,
        -1,-1,-1, -1,-1, 1, -1, 1, 1, -1,-1,-1, -1, 1, 1, -1, 1,-1,
         1,-1, 1,  1,-1,-1,  1, 1,-1,  1,-1, 1,  1, 1,-1,  1, 1, 1,
        -1, 1, 1,  1, 1, 1,  1, 1,-1, -1, 1, 1,  1, 1,-1, -1, 1,-1,
        -1,-1,-1,  1,-1,-1,  1,-1, 1, -1,-1,-1,  1,-1, 1, -1,-1, 1
    ]);

    function createShader(gl, type, source) {
        const shader=gl.createShader(type); gl.shaderSource(shader, source); gl.compileShader(shader);
        if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(shader)||'Shader compilation failed');
        return shader;
    }

    class Renderer {
        constructor(canvas, options) {
            this.canvas=canvas; this.options=options||{}; this.angle=0; this.fanAngle=0; this.last=0; this.raf=0;
            this.reducedMotion=window.matchMedia&&window.matchMedia('(prefers-reduced-motion: reduce)');
            this.gl=canvas.getContext('webgl', { alpha:true, antialias:true, powerPreference:'low-power' });
            if (!this.gl) throw new Error('WebGL unavailable');
            const gl=this.gl;
            const vs=createShader(gl,gl.VERTEX_SHADER,'attribute vec3 p; uniform mat4 mvp; void main(){gl_Position=mvp*vec4(p,1.0);}');
            const fs=createShader(gl,gl.FRAGMENT_SHADER,'precision mediump float; uniform vec4 color; void main(){gl_FragColor=color;}');
            this.program=gl.createProgram(); gl.attachShader(this.program,vs); gl.attachShader(this.program,fs); gl.linkProgram(this.program);
            if(!gl.getProgramParameter(this.program,gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(this.program)||'Program link failed');
            this.pos=gl.getAttribLocation(this.program,'p'); this.mvp=gl.getUniformLocation(this.program,'mvp'); this.color=gl.getUniformLocation(this.program,'color');
            this.buffer=gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER,this.buffer); gl.bufferData(gl.ARRAY_BUFFER,cubeVertices,gl.STATIC_DRAW);
            gl.useProgram(this.program); gl.enableVertexAttribArray(this.pos); gl.vertexAttribPointer(this.pos,3,gl.FLOAT,false,0,0);
            gl.enable(gl.DEPTH_TEST); gl.enable(gl.CULL_FACE); gl.cullFace(gl.BACK); gl.enable(gl.BLEND); gl.blendFunc(gl.SRC_ALPHA,gl.ONE_MINUS_SRC_ALPHA);
            this.frame=this.frame.bind(this); this.raf=requestAnimationFrame(this.frame);
        }
        update(options){ this.options=Object.assign({},this.options,options||{}); }
        resize(){
            const dpr=Math.min(window.devicePixelRatio||1,1.5), w=Math.max(1,Math.round(this.canvas.clientWidth*dpr)), h=Math.max(1,Math.round(this.canvas.clientHeight*dpr));
            if(this.canvas.width!==w||this.canvas.height!==h){this.canvas.width=w;this.canvas.height=h;}
            this.gl.viewport(0,0,w,h); return w/h;
        }
        drawBox(viewProj, model, color){
            const gl=this.gl; gl.uniformMatrix4fv(this.mvp,false,new Float32Array(mat4Mul(viewProj,model))); gl.uniform4fv(this.color,new Float32Array(color)); gl.drawArrays(gl.TRIANGLES,0,36);
        }
        rotor(viewProj,x,y,z,radius,angle){
            const accent=[0.23,0.58,0.96,0.92], dark=[0.10,0.15,0.24,0.96];
            this.drawBox(viewProj,compose(translate(x,y,z),scale(radius*0.18,radius*0.18,.16)),accent);
            for(let i=0;i<6;i++){
                const a=angle+(i*Math.PI/3);
                this.drawBox(viewProj,compose(translate(x,y,z),rotateZ(a),translate(radius*.52,0,0),scale(radius*.46,radius*.12,.08)),dark);
            }
        }
        cpu(viewProj){
            this.drawBox(viewProj,compose(translate(0,0,0),scale(1.15,1.2,.58)),[.16,.20,.28,.95]);
            for(let i=-4;i<=4;i++) this.drawBox(viewProj,compose(translate(0,i*.21,.72),scale(1.2,.045,.09)),[.37,.43,.52,.85]);
            this.rotor(viewProj,0,0,1.05,.82,this.fanAngle);
        }
        gpu(viewProj){
            this.drawBox(viewProj,compose(translate(0,0,0),scale(2.15,.92,.34)),[.12,.16,.23,.98]);
            this.drawBox(viewProj,compose(translate(0,-.98,-.05),scale(2.05,.08,.28)),[.27,.34,.42,.95]);
            const count=Math.max(1,Math.min(3,Number(this.options.fanCount)||1));
            const spacing=count===1?0:1.3;
            for(let i=0;i<count;i++) this.rotor(viewProj,(i-(count-1)/2)*spacing,0,.43,.62,this.fanAngle+i*.3);
        }
        caseFan(viewProj){
            this.drawBox(viewProj,compose(scale(1.38,.10,1.38)),[.16,.21,.29,.45]);
            this.rotor(viewProj,0,.18,0,1.05,this.fanAngle);
        }
        pump(viewProj){
            this.drawBox(viewProj,compose(scale(.95,.95,.55)),[.12,.17,.24,.98]);
            this.drawBox(viewProj,compose(translate(0,0,.65),scale(.62,.62,.18)),[.22,.55,.90,.75]);
            this.rotor(viewProj,0,0,.88,.48,this.fanAngle);
        }
        frame(now){
            if(!this.canvas.isConnected){this.destroy();return;}
            const dt=Math.min(.05,(now-this.last)/1000||0); this.last=now;
            const reduced=this.reducedMotion&&this.reducedMotion.matches;
            if(!reduced) this.angle+=dt*.18;
            const rpm=Math.max(0,Number(this.options.rpm)||0);
            if(!reduced&&rpm>0) this.fanAngle=(this.fanAngle+dt*Math.min(30,Math.max(1.2,rpm/110)))%(Math.PI*2);
            const aspect=this.resize(), gl=this.gl; gl.clearColor(0,0,0,0); gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);
            const projection=perspective(Math.PI/4,aspect,.1,30);
            const view=compose(translate(0,0,-6.2),rotateX(-.22),rotateY(this.angle-.55));
            const vp=mat4Mul(projection,view);
            const type=String(this.options.type||'fan').toLowerCase();
            if(type==='gpu')this.gpu(vp); else if(type==='cpu')this.cpu(vp); else if(type==='pump')this.pump(vp); else this.caseFan(vp);
            this.raf=requestAnimationFrame(this.frame);
        }
        destroy(){ if(this.raf)cancelAnimationFrame(this.raf); this.raf=0; try{this.gl.deleteBuffer(this.buffer);this.gl.deleteProgram(this.program);}catch(_){} }
    }

    window.FanHardwareVisualizer={
        mount(canvas,options){
            if(!canvas)return false;
            let renderer=instances.get(canvas);
            try{
                if(renderer){renderer.update(options);return true;}
                renderer=new Renderer(canvas,options); instances.set(canvas,renderer); canvas.dataset.webgl='true'; return true;
            }catch(error){ canvas.dataset.webgl='false'; try{console.warn('Fan WebGL visualizer unavailable',error);}catch(_){} return false; }
        },
        destroy(canvas){ const renderer=instances.get(canvas); if(renderer){renderer.destroy();instances.delete(canvas);} }
    };
})();
