# Netseller Support Bot

Designed and developed a fully functional Telegram bot that provided answers to commonly asked questions by various merchant support groups, including looking up the status of transactions via a third-party API. Built using C#, deployed to an Azure WebApp.

## Features

- **Order Status Lookup:**  
  Prompts users for an Order ID and queries backend APIs to return the status and transaction hash.

- **Descriptors Access:**  
  Inline menu allows searching descriptors or downloading them from a text file.

- **Blacklist Detection:**  
  Lets users submit email, phone, or card info for blacklist checks via dynamic inline buttons.

- **Retry Logic & Friendly UX:**  
  Guides users through correction attempts when invalid inputs are given (e.g., wrong Order ID or email).

- **Inline Keyboard Navigation:**  
  Clean and interactive menu system built with Telegram’s inline buttons.

---

## Tech Stack

- **Language:** C# (.NET 8)
- **Telegram SDK:** Telegram.Bot 19
- **Hosting:** Azure Functions
- **API Integration:** RESTful backend
- **Storage:** Text files for mappings and descriptors

---

- Follow inline buttons like:
  - **Payment Status**
  - **Server Status**
  - **Blacklist**
  - **Descriptors**
---

## License

MIT License
