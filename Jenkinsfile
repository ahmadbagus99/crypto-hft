// Jenkins pipeline: builds and deploys the BTCUSDT HFT stack to the VPS whenever the
// tracked branch changes. The actual build happens on the VPS via docker compose, so the
// Jenkins agent only needs SSH access — no .NET/Node toolchain required on the agent.
//
// Prerequisites (one-time setup in Jenkins):
//   1. Add an "SSH Username with private key" credential with id 'vps-ssh' for root@VPS.
//      (Append the matching public key to /root/.ssh/authorized_keys on the VPS.)
//   2. Point the job at this repo. A GitHub webhook -> "GitHub hook trigger" is ideal;
//      the pollSCM trigger below is a fallback if no webhook is configured.
//
// Optional Jenkins string/secret credentials injected into the container env on deploy:
//   'binance-api-key', 'binance-api-secret', 'anthropic-api-key', 'app-password'
//   (All optional — the app also reads keys saved via the dashboard, now DB-persisted.)

pipeline {
    agent any

    parameters {
        string(
            name: 'DEPLOY_BRANCH',
            defaultValue: 'main',
            trim: true,
            description: 'Git branch yang akan di-checkout dan di-deploy ke VPS'
        )
    }

    options {
        timeout(time: 30, unit: 'MINUTES')
        disableConcurrentBuilds()
        timestamps()
    }

    triggers {
        pollSCM('H/5 * * * *')
    }

    environment {
        VPS_HOST     = '217.216.72.181'
        VPS_USER     = 'root'
        DEPLOY_PATH  = '/opt/crypto-hft'
        REPO_URL     = 'https://github.com/ahmadbagus99/crypto-hft.git'
        COMPOSE_FILE = 'docker-compose.prod.yml'
        API_HEALTH   = 'http://localhost:5006/health'
    }

    stages {
        stage('Checkout') {
            steps {
                checkout scm
            }
        }

        stage('Validate deploy branch') {
            steps {
                sh '''
                    if [ -z "${DEPLOY_BRANCH}" ]; then
                        echo "DEPLOY_BRANCH tidak boleh kosong"
                        exit 1
                    fi
                    if ! printf '%s' "${DEPLOY_BRANCH}" | grep -Eq '^[A-Za-z0-9][A-Za-z0-9._/-]*$'; then
                        echo "DEPLOY_BRANCH mengandung karakter yang tidak diizinkan"
                        exit 1
                    fi
                    git check-ref-format --branch "${DEPLOY_BRANCH}" >/dev/null
                    echo "Branch yang akan di-deploy: ${DEPLOY_BRANCH}"
                '''
            }
        }

        stage('Deploy to VPS') {
            steps {
                sshagent(credentials: ['creatio-server']) {
                    sh '''
                        ssh -o StrictHostKeyChecking=no ${VPS_USER}@${VPS_HOST} bash -se <<EOF
                            set -euo pipefail
                            if [ ! -d "${DEPLOY_PATH}/.git" ]; then
                                git clone "${REPO_URL}" "${DEPLOY_PATH}"
                            fi
                            cd "${DEPLOY_PATH}"
                            git fetch --prune origin "+refs/heads/${DEPLOY_BRANCH}:refs/remotes/origin/${DEPLOY_BRANCH}"
                            git checkout -B "${DEPLOY_BRANCH}" "origin/${DEPLOY_BRANCH}"
                            git reset --hard "origin/${DEPLOY_BRANCH}"
                            docker compose -f "${COMPOSE_FILE}" up --build -d
                            docker image prune -f
EOF
                    '''
                }
            }
        }

        stage('Health check') {
            steps {
                sshagent(credentials: ['creatio-server']) {
                    sh '''
                        ssh -o StrictHostKeyChecking=no ${VPS_USER}@${VPS_HOST} bash -se <<EOF
                            set -euo pipefail
                            for i in \\$(seq 1 12); do
                                if curl -fsS "${API_HEALTH}" >/dev/null 2>&1; then
                                    echo "API healthy"
                                    exit 0
                                fi
                                echo "Waiting for API... (\\$i/12)"
                                sleep 5
                            done
                            echo "API did not become healthy in time"
                            docker compose -f "${DEPLOY_PATH}/${COMPOSE_FILE}" logs --tail 50 api || true
                            exit 1
EOF
                    '''
                }
            }
        }
    }

    post {
        success {
            echo "Deploy branch ${params.DEPLOY_BRANCH} sukses: frontend http://${VPS_HOST}:5005 | api http://${VPS_HOST}:5006"
        }
        failure {
            echo 'Deploy gagal. Cek log stage di atas.'
        }
    }
}
