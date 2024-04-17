from flask import Flask, request
import os
import signal
import subprocess

app = Flask(__name__)

nethermind_process = None

@app.route('/set_time', methods=['POST'])
def set_time():
    fake_time = request.json.get('fake_time')
    if fake_time:
        os.environ['FAKETIME'] = fake_time
        restart_nethermind()
        return {'message': f'Fake time set to {fake_time}'}
    else:
        return {'error': 'No fake time provided'}, 400

def restart_nethermind():
    global nethermind_process
    if nethermind_process:
        nethermind_process.send_signal(signal.SIGTERM)
        nethermind_process.wait()

    env = os.environ.copy()
    env['LD_PRELOAD'] = '/usr/lib/x86_64-linux-gnu/faketime/libfaketime.so.1'
    env['FAKETIME_NO_CACHE'] = '1'

    nethermind_process = subprocess.Popen([
        './Nethermind.Runner',
        '--config', 'circles'
    ], env=env, cwd='/nethermind', preexec_fn=os.setsid)

if __name__ == '__main__':
    restart_nethermind()
    app.run(host='0.0.0.0', port=5000)
