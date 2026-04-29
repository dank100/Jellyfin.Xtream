export default function (view) {
  const createChannelRow = (channel, overrides, epgSources, Xtream) => {
    const tr = document.createElement('tr');
    tr.dataset['channelId'] = channel.Id;

    let td = document.createElement('td');
    const number = document.createElement('input');
    number.type = 'number';
    number.setAttribute('is', 'emby-input');
    number.placeholder = channel.Number;
    number.value = overrides.Number ?? '';
    number.onchange = () => number.value ?
      overrides.Number = parseInt(number.value) :
      delete overrides.Number;
    td.appendChild(number);
    const numLabel = document.createElement('span');
    numLabel.className = 'xtream-ch-label';
    numLabel.textContent = channel.Number;
    td.appendChild(numLabel);
    tr.appendChild(td);

    td = document.createElement('td');
    const name = document.createElement('input');
    name.type = 'text';
    name.setAttribute('is', 'emby-input');
    name.placeholder = channel.Name;
    name.value = overrides.Name ?? '';
    name.onchange = () => name.value ?
      overrides.Name = name.value :
      delete overrides.Name;
    td.appendChild(name);
    const nameLabel = document.createElement('span');
    nameLabel.className = 'xtream-ch-label';
    nameLabel.textContent = channel.Name;
    td.appendChild(nameLabel);
    tr.appendChild(td);

    td = document.createElement('td');
    const image = document.createElement('input');
    image.type = 'text';
    image.setAttribute('is', 'emby-input');
    image.placeholder = channel.LogoUrl;
    image.value = overrides.LogoUrl ?? '';
    image.onchange = () => image.value ?
      overrides.LogoUrl = image.value :
      delete overrides.LogoUrl;
    td.appendChild(image);
    tr.appendChild(td);

    td = document.createElement('td');
    const epgTz = document.createElement('input');
    epgTz.type = 'text';
    epgTz.setAttribute('is', 'emby-input');
    epgTz.placeholder = 'UTC';
    epgTz.value = overrides.EpgTimezone ?? '';
    epgTz.onchange = () => epgTz.value ?
      overrides.EpgTimezone = epgTz.value :
      delete overrides.EpgTimezone;
    td.appendChild(epgTz);
    tr.appendChild(td);

    td = document.createElement('td');
    const epgSource = document.createElement('select');
    epgSource.setAttribute('is', 'emby-select');
    const defaultOpt = document.createElement('option');
    defaultOpt.value = '';
    defaultOpt.textContent = 'Xtream (default)';
    epgSource.appendChild(defaultOpt);
    (epgSources || []).forEach(src => {
      const opt = document.createElement('option');
      opt.value = src.Id;
      opt.textContent = src.Name;
      epgSource.appendChild(opt);
    });
    epgSource.value = overrides.EpgSourceId ?? '';
    epgSource.onchange = () => epgSource.value ?
      overrides.EpgSourceId = epgSource.value :
      delete overrides.EpgSourceId;
    td.appendChild(epgSource);
    tr.appendChild(td);

    td = document.createElement('td');
    const xmltvChId = document.createElement('input');
    xmltvChId.type = 'text';
    xmltvChId.className = 'emby-input';
    xmltvChId.placeholder = 'Type to search...';
    xmltvChId.style.cssText = 'width:100%;padding:.35em .5em;font-size:.9em;box-sizing:border-box;';
    xmltvChId.value = overrides.XmltvChannelId ?? '';
    xmltvChId.setAttribute('list', `xmltv-list-${channel.Id}`);
    const datalist = document.createElement('datalist');
    datalist.id = `xmltv-list-${channel.Id}`;
    td.appendChild(xmltvChId);
    td.appendChild(datalist);

    const xmltvLabel = document.createElement('span');
    xmltvLabel.className = 'xtream-ch-label';
    xmltvLabel.textContent = '';
    td.appendChild(xmltvLabel);

    xmltvChId.addEventListener('input', () => {
      if (xmltvChId.value) {
        overrides.XmltvChannelId = xmltvChId.value;
      } else {
        delete overrides.XmltvChannelId;
      }
      // Update label with display name
      const match = xmltvChId._channels?.find(ch => ch.Id === xmltvChId.value);
      xmltvLabel.textContent = match ? match.DisplayName : '';
    });
    tr.appendChild(td);

    // Auto-match: find best XMLTV channel for this Xtream channel name
    const autoMatch = (channels) => {
      const chName = (channel.Name || '').toLowerCase().replace(/[^a-z0-9]/g, '');
      if (!chName) return null;
      // Exact match on ID
      let best = channels.find(c => c.Id.toLowerCase().replace(/[^a-z0-9]/g, '').includes(chName));
      if (best) return best.Id;
      // Exact match on display name
      best = channels.find(c => c.DisplayName.toLowerCase().replace(/[^a-z0-9]/g, '') === chName);
      if (best) return best.Id;
      // Substring match on display name
      best = channels.find(c => c.DisplayName.toLowerCase().replace(/[^a-z0-9]/g, '').includes(chName));
      if (best) return best.Id;
      // Reverse: channel name contained in display name
      best = channels.find(c => chName.includes(c.DisplayName.toLowerCase().replace(/[^a-z0-9]/g, '')));
      if (best) return best.Id;
      return null;
    };

    // When EPG source changes, fetch XMLTV channels for the datalist
    const loadXmltvChannels = (sourceId) => {
      datalist.innerHTML = '';
      xmltvChId._channels = [];
      if (!sourceId) return;
      Xtream.fetchJson(`Xtream/EpgChannels/${sourceId}`).then(channels => {
        xmltvChId._channels = channels;
        channels.forEach(ch => {
          const opt = document.createElement('option');
          opt.value = ch.Id;
          opt.label = `${ch.DisplayName} (${ch.Id})`;
          datalist.appendChild(opt);
        });
        // Restore saved value or auto-match
        const savedValue = overrides.XmltvChannelId ?? '';
        if (savedValue) {
          xmltvChId.value = savedValue;
          const match = channels.find(ch => ch.Id === savedValue);
          xmltvLabel.textContent = match ? match.DisplayName : '';
        } else {
          const matched = autoMatch(channels);
          if (matched) {
            xmltvChId.value = matched;
            overrides.XmltvChannelId = matched;
            const match = channels.find(ch => ch.Id === matched);
            xmltvLabel.textContent = match ? `⚡ ${match.DisplayName}` : '';
          }
        }
      }).catch((err) => { console.error('Failed to load XMLTV channels', err); });
    };
    epgSource.addEventListener('change', () => loadXmltvChannels(epgSource.value));
    // Load initial channels if source is already set
    if (overrides.EpgSourceId) loadXmltvChannels(overrides.EpgSourceId);

    return tr;
  };

  view.addEventListener("viewshow", () => import(
    ApiClient.getUrl("web/ConfigurationPage", {
      name: "Xtream.js",
    })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs(2);

    const getConfig = ApiClient.getPluginConfiguration(pluginId);
    const table = view.querySelector('#LiveChannels');
    Dashboard.showLoadingMsg();
    Promise.all([
      getConfig,
      Xtream.fetchJson('Xtream/LiveTv'),
    ]).then(([config, channels]) => {
      const data = config.LiveTvOverrides;
      const epgSources = config.EpgSources || [];
      for (const channel of channels) {
        data[channel.Id] ??= {};
        const row = createChannelRow(channel, data[channel.Id], epgSources, Xtream);
        table.appendChild(row);
      }
      Dashboard.hideLoadingMsg();

      view.querySelector('#XtreamLiveOverridesForm').addEventListener('submit', (e) => {
        Dashboard.showLoadingMsg();

        ApiClient.getPluginConfiguration(pluginId).then((config) => {
          config.LiveTvOverrides = Xtream.filter(
            data,
            overrides => Object.keys(overrides).length > 0
          );
          ApiClient.updatePluginConfiguration(pluginId, config).then((result) => {
            Dashboard.processPluginConfigurationUpdateResult(result);
          });
        });

        e.preventDefault();
        return false;
      });
    });
  }));
}