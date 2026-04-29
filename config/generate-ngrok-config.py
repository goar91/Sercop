#!/usr/bin/env python3
import os
import sys

ngrok_authtoken = os.environ.get('NGROK_AUTHTOKEN', '')
crm_domain = os.environ.get('CRM_NGROK_DOMAIN', '')
nutrition_domain = os.environ.get('NUTRITION_NGROK_DOMAIN', '')

config = f"""version: 3
authtoken: {ngrok_authtoken}
tunnels:
  crm:
    proto: http
    addr: host.docker.internal:8080
    domain: {crm_domain}
  nutrition:
    proto: http
    addr: host.docker.internal:8080
    domain: {nutrition_domain}
"""

with open('/tmp/ngrok.yml', 'w') as f:
    f.write(config)

print("Configuración de ngrok generada:")
print(config)

# Ejecutar ngrok
os.execvp('ngrok', ['ngrok', 'start', '--all', '--config=/tmp/ngrok.yml'])
